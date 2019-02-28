using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Microsoft.Azure.Management.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent.Core;
using Microsoft.Azure.Management.ResourceManager.Fluent;
using Microsoft.Azure.Management.Compute.Fluent;
using Microsoft.Azure.Cosmos.Table;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;

namespace httpTriggerAutoScale
{
    public static class ScaleDown
    {
        //!!!!!!!!!!!!!!Important note!!!!!!!!!!!!!
        //Before you will start, you will need a file my.azureauth with Rbac credentials here is a command to generate it
        //az ad sp create-for-rbac --sdk-auth > my.azureauth
        //
        const string CpuMetricName = "/builtin/processor/percentprocessortime";
        const string DiskMetricName = "/builtin/disk/bytespersecond";

        [FunctionName("ScaleDown")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req,
            ILogger log, ExecutionContext context)
        {           
            string logString;

            #region Capture init strings

            DateTime TimeOfCreation;
            CultureInfo provider = CultureInfo.InvariantCulture;
            string ScaleSetId = Environment.GetEnvironmentVariable("ScaleSetId");
            int LookupTimeInMinutes = int.Parse(Environment.GetEnvironmentVariable("LookupTimeInMin"));
            int CPUTreshold = int.Parse(Environment.GetEnvironmentVariable("CPUTresholdInPercent"));
            int DiskTresholdBytes = int.Parse(Environment.GetEnvironmentVariable("DiskTresholdBytes"));
            string TablePrefix = Environment.GetEnvironmentVariable("TablePrefix");
            string StorageAccountConnectionString = Environment.GetEnvironmentVariable("StorageAccountConnectionString");
            bool parseddate = DateTime.TryParseExact(Environment.GetEnvironmentVariable("TimeOfCreation"), "yyyy-MM-ddTHH:mm:ssZ", provider,DateTimeStyles.AdjustToUniversal, out TimeOfCreation);
            TimeSpan StartupDelayInMin = TimeSpan.FromMinutes(double.Parse(Environment.GetEnvironmentVariable("StartupDelayInMin")));
            var timeInPast = DateTime.UtcNow.Subtract(StartupDelayInMin);

            if (String.IsNullOrEmpty(ScaleSetId) || LookupTimeInMinutes <= 0 || CPUTreshold <= 0 || DiskTresholdBytes <= 0 ||
                String.IsNullOrEmpty(TablePrefix) || String.IsNullOrEmpty(StorageAccountConnectionString) || !parseddate)
            {
                logString = $"HTTP trigger function started but not all init params are set";
                log.LogInformation(logString);
                return (ActionResult)new OkObjectResult(logString);
            }
            #endregion

            //Checking is the time right to start scaling, probably initial delay is not come yet
            if (TimeOfCreation.ToUniversalTime() <= timeInPast)
            {
                log.LogInformation("HTTP trigger function processed a request and will try to adjust scaleset. " + ScaleSetId);

                var azure = AzureAuth(context);

                var metrics = GetMetricsFromTable(StorageAccountConnectionString, LookupTimeInMinutes, TablePrefix, ScaleSetId);

                var instances = GetInstancesToKill(metrics, CPUTreshold, DiskTresholdBytes);

                var dealocated = await DealocateInstances(instances, ScaleSetId, azure, log);

                logString = $"Done, number of dealocated instances {dealocated.Count.ToString()}: {String.Join(", ", dealocated.ToArray())}";
                log.LogInformation(logString);
            }
            else {

                logString = $"HTTP trigger function processed but Its to early to scale cluster time not come.";
                log.LogInformation(logString);
            }

            return (ActionResult)new OkObjectResult(logString);         
        }

        /// <summary>
        /// Dealocating instances in scale-set based on instances list of ids
        /// </summary>
        private static async Task<List<string>> DealocateInstances(List<string> Instances, string scalesetid, IAzure AzureInstance, ILogger log) {

            var scaleset = AzureInstance.VirtualMachineScaleSets.GetById(scalesetid);
            
            var instances = scaleset.VirtualMachines.List();

            List<string> dealocatedInstances = new List<string>();

            foreach (var ins in instances)
            {
                if (Instances.Contains(ins.ComputerName))
                {
                    try
                    {
                        //Check if instances not deallocated before
                        if (!ins.PowerState.Value.ToLower().Contains("deallocated"))
                        {
                            await ins.DeallocateAsync();
                            dealocatedInstances.Add(ins.ComputerName);
                        }
                    }
                    catch (SystemException e) {

                        log.LogInformation("Error during dealocation of node:" + ins.ComputerName + " with error:" + e.Message);
                    }
                }
            }

            return dealocatedInstances;
        }

        /// <summary>
        /// Selecting intances that need to be killed based on low metrics
        /// </summary>
        private static List<string> GetInstancesToKill(List<WadMetric> Metrics, int CPUTreshold, int DiskTreshold) {
            List<string> instances = new List<string>();

            if (Metrics != null)
            {
                if (Metrics.Count > 0)
                {
                    //Calculate average by metric grouped by Host
                    var groupedResult = Metrics.GroupBy(t => new { Host = t.Host, Metric = t.CounterName })
                                   .Select(g => new
                                   {
                                       HostID = g.Key.Host,
                                       MetricID = g.Key.Metric,
                                       Average =g.Average(y=>y.Average)  
                                   });
                    //select intances with less then CPUTreshold and Disk
                    var insCPU = groupedResult.Where(x => x.MetricID == CpuMetricName).Where(y => y.Average <= CPUTreshold).Select(x => x.HostID);
                    var insDisk = groupedResult.Where(x => x.MetricID == DiskMetricName).Where(y => y.Average <= DiskTreshold).Select(x => x.HostID);

                    var outlist = insCPU.Select(i => i.ToString()).Intersect(insDisk);

                    instances = new List<string>(outlist);
                }

            }           

            return instances;
        }

        /// <summary>
        /// Requesting Metrics from azure tables for each node in scale-set
        /// </summary>
        private static List<WadMetric> GetMetricsFromTable(string StorageAccountConnectionString,int LookupTimeInMinutes,  string TablePrefix, string ScalesetResourceID) {

            List<WadMetric> resultList = new List<WadMetric>();

            CloudStorageAccount storageAccount = CloudStorageAccount.Parse(StorageAccountConnectionString);
            CloudTableClient tableClient = storageAccount.CreateCloudTableClient();

            CloudTable table = tableClient.ListTables(TablePrefix).First();

            if (table == null) {

                throw new SystemException("There is no table in storage account with prefix:" + TablePrefix);
            }

            //CloudTable table = tableClient.GetTableReference(TableName);

            //Timeback in mins
            var minutesBack = TimeSpan.FromMinutes(LookupTimeInMinutes);
            var timeInPast = DateTime.UtcNow.Subtract(minutesBack);
            DateTimeOffset qdate = new DateTimeOffset(timeInPast);

            //TODO add ROWKEY to encrease performance.
            TableQuery<WadMetric> rangeQuery = new TableQuery<WadMetric>().Where(
                TableQuery.CombineFilters(
                    TableQuery.CombineFilters(
                        TableQuery.GenerateFilterCondition("PartitionKey", QueryComparisons.Equal, ScalesetResourceID.Replace("/", ":002F").Replace("-", ":002D").Replace(".", ":002E")),
                        TableOperators.And,
                        TableQuery.GenerateFilterConditionForDate("TIMESTAMP", QueryComparisons.GreaterThanOrEqual, qdate)),
                    TableOperators.And,
                    TableQuery.CombineFilters(
                        TableQuery.GenerateFilterCondition("CounterName", QueryComparisons.Equal, CpuMetricName),
                        TableOperators.Or,
                        TableQuery.GenerateFilterCondition("CounterName", QueryComparisons.Equal, DiskMetricName)
                    )));
                  
            var result = table.ExecuteQuery(rangeQuery);

            foreach (var entity in result)
            {
                resultList.Add(entity);

                //Console.WriteLine("{0}, {1}\t{2}\t{3}", entity.PartitionKey, entity.RowKey,
                //    entity.Host, entity.Average);
            }

            return resultList;
        }

        /// <summary>
        /// Auth in azure api
        /// </summary>
        private static IAzure AzureAuth(ExecutionContext context) {

            var path = System.IO.Path.Combine(context.FunctionAppDirectory, "my.azureauth");
            var credentials = SdkContext.AzureCredentialsFactory.FromFile(path);

            var azure = Azure
                .Configure()
                .WithLogLevel(HttpLoggingDelegatingHandler.Level.Basic)
                .Authenticate(credentials)
                .WithDefaultSubscription();

            return azure;
        }
    }   

    /// <summary>
    /// Class to store entities from azure table metrics
    /// </summary>
    public class WadMetric:TableEntity
    {
        public WadMetric(string PartitionKey, string RowKey)
        {
            this.PartitionKey = PartitionKey;
            this.RowKey = RowKey;
        }
        public WadMetric() { }
        public double Average { get; set; }
        public int Count { get; set; }
        public string CounterName { get; set; }
        public string DeploymentId { get; set; }
        public string Host { get; set; }
        public double Last { get; set; }
        public double Maximum { get; set; }
        public double Minimum { get; set; }
        public DateTime TIMESTAMP { get; set; }
        public double Total { get; set; }
    }

}
