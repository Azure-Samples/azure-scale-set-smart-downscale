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
    class Utils
    {
        const string CpuMetricName = "/builtin/processor/percentprocessortime";
        const string DiskMetricName = "/builtin/disk/bytespersecond";
        
        /// <summary>
        /// Dealocating instances in scale-set based on instances list of ids
        /// </summary>
        internal static List<string> DealocateInstances(List<string> Instances, string scalesetid, IAzure AzureInstance, ILogger log)
        {

            var scaleset = AzureInstance.VirtualMachineScaleSets.GetById(scalesetid);

            var instances = scaleset.VirtualMachines.List();

            List<string> dealocatedInstances = new List<string>();

            List<Task> TaskList = new List<Task>();

            foreach (var ins in instances)
            {
                if (Instances.Contains(ins.ComputerName))
                {
                    try
                    {
                        //Check if instances not deallocated before or in dealocatting state
                        if (ins.PowerState != PowerState.Deallocated && ins.PowerState != PowerState.Deallocating)
                        {
                            TaskList.Add(ins.DeleteAsync());

                            dealocatedInstances.Add(ins.ComputerName);
                        }
                    }
                    catch (SystemException e)
                    {

                        log.LogInformation("Error during dealocation of node:" + ins.ComputerName + " with error:" + e.Message);
                    }
                }
            }

            Task.WaitAll(TaskList.ToArray());

            return dealocatedInstances;
        }

        /// <summary>
        /// Selecting intances that need to be killed based on low metrics
        /// </summary>
        internal static List<string> GetInstancesToKill(List<WadMetric> Metrics, int CPUTreshold, int DiskTreshold, ILogger log)
        {
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
                                       Average = g.Average(y => y.Average)
                                   });

                    //select intances with less then CPUTreshold and Disk
                    var insCPU = groupedResult.Where(x => x.MetricID == CpuMetricName).Where(y => y.Average <= CPUTreshold).Select(x => x.HostID);
                    var insDisk = groupedResult.Where(x => x.MetricID == DiskMetricName).Where(y => y.Average <= DiskTreshold).Select(x => x.HostID);

                    //checking for NULLS
                    if (insCPU == null || insDisk == null) {

                        log.LogInformation("All instances are busy");
                        instances = new List<string>();

                    }
                    else
                    {
                        log.LogInformation($"Number of nodes that have low CPU:{insCPU.Count()}. Names:{String.Join(", ", insCPU.ToArray())}");
                        log.LogInformation($"Number of nodes that have low Disk usage:{insDisk.Count()}. Names:{String.Join(", ", insDisk.ToArray())}");

                        var outlist = insCPU.Select(i => i.ToString()).Intersect(insDisk);

                        instances = new List<string>(outlist);
                    }
                }
            }

            return instances;
        }

        /// <summary>
        /// Requesting Metrics from azure tables for each node in scale-set
        /// </summary>
        internal static List<WadMetric> GetMetricsFromTable(string StorageAccountConnectionString, int LookupTimeInMinutes, string TablePrefix, string ScalesetResourceID)
        {

            List<WadMetric> resultList = new List<WadMetric>();

            CloudStorageAccount storageAccount = CloudStorageAccount.Parse(StorageAccountConnectionString);
            CloudTableClient tableClient = storageAccount.CreateCloudTableClient();

            CloudTable table = tableClient.ListTables(TablePrefix).Last();
            

            if (table == null)
            {

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
        internal static IAzure AzureAuth(ExecutionContext context)
        {

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
}
