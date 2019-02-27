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

namespace httpTriggerAutoScale
{
    public static class ScaleDown
    {
        const int LookupTimeInMinutes = 5;
        const int CPUTreshold = 5;
        const string scalesetid = "/subscriptions/3c5814b6-2535-4425-a242-9a3bd1718f29/resourceGroups/turbotest/providers/Microsoft.Compute/virtualMachineScaleSets/turboset";
        const string tablePrefix = "WADMetricsPT1M";
        //const string tablename = "WADMetricsPT1MP10DV2S20190219";
        const string storageAccountConnectionString = @"BlobEndpoint=https://turborenderstorage.blob.core.windows.net/;QueueEndpoint=https://turborenderstorage.queue.core.windows.net/;FileEndpoint=https://turborenderstorage.file.core.windows.net/;TableEndpoint=https://turborenderstorage.table.core.windows.net/;SharedAccessSignature=sv=2018-03-28&ss=bfqt&srt=sco&sp=rwdlacup&se=2025-02-27T00:51:39Z&st=2019-02-26T16:51:39Z&spr=https&sig=YRTNQFZ2E8YV4S05WSbUDF3WWaAQWR0vYGKJAsHt5iE%3D";

        [FunctionName("ScaleDown")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req,
            ILogger log, ExecutionContext context)
        {
            log.LogInformation("C# HTTP trigger function processed a request and will try to adjust scaleset. "+ scalesetid);
            
            var azure = AzureAuth(context);              
            
            var metrics = GetMetricsFromTable(storageAccountConnectionString, tablePrefix, scalesetid);

            var instances = GetInstancesToKill(metrics);

            var dealocated = await DealocateInstances(instances, azure, log);

            string logString = $"Done, number of dealocated instances {dealocated.Count.ToString()}: {String.Join(", ", dealocated.ToArray())}";
            log.LogInformation(logString);

            return (ActionResult)new OkObjectResult(logString);         
        }

        //dealocating instances in scale-set based on instances list of ids
        private static async Task<List<string>> DealocateInstances(List<string> Instances, IAzure AzureInstance, ILogger log) {

            var scaleset = AzureInstance.VirtualMachineScaleSets.GetById(scalesetid);
            var instances = scaleset.VirtualMachines.List();

            List<string> dealocatedInstances = new List<string>();

            foreach (var ins in instances)
            {
                if (Instances.Contains(ins.ComputerName))
                {
                    try
                    {
                        //Task.Run(async () => await ins.DeallocateAsync()).ConfigureAwait(false).GetAwaiter().GetResult();
                        await ins.DeallocateAsync();
                        dealocatedInstances.Add(ins.ComputerName);
                    }
                    catch (SystemException e) {

                        log.LogInformation("Error during dealocation of node:" + ins.ComputerName + " with error:" + e.Message);
                    }
                }
            }

            return dealocatedInstances;
        }

        //Selecting intances that need to be killed based on low metrics
        private static List<string> GetInstancesToKill(List<WadMetric> Metrics) {
            List<string> instances;

            //Calculate average by metric grouped by Host
            var groupedResult = Metrics.GroupBy(t => new { Host = t.Host })
                           .Select(g => new
                           {
                               Average = g.Average(p => p.Average),
                               ID = g.Key.Host
                           });
            //select intances with less thenCPUTreshold
            var ins=  groupedResult.Where(x => x.Average <= CPUTreshold);
            instances = new List<string>(ins.Select(x=> x.ID));

            return instances;
        } 

        //Requesting Metrics from azure tables for each node in scale-set
        private static List<WadMetric> GetMetricsFromTable(string StorageAccountConnectionString, string TablePrefix, string ScalesetResourceID) {

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
                TableQuery.CombineFilters(TableQuery.CombineFilters(
                        TableQuery.GenerateFilterCondition("PartitionKey",
                                        QueryComparisons.Equal,
                                        ScalesetResourceID.Replace("/", ":002F").Replace("-", ":002D").Replace(".", ":002E")),
                        TableOperators.And,
                        TableQuery.GenerateFilterCondition("CounterName", QueryComparisons.Equal, "/builtin/processor/percentprocessortime")),
                    TableOperators.And,
                    TableQuery.GenerateFilterConditionForDate("TIMESTAMP", QueryComparisons.GreaterThanOrEqual, qdate)));
                  
            var result = table.ExecuteQuery(rangeQuery);

            foreach (var entity in result)
            {
                resultList.Add(entity);

                //Console.WriteLine("{0}, {1}\t{2}\t{3}", entity.PartitionKey, entity.RowKey,
                //    entity.Host, entity.Average);
            }

            return resultList;
        }

        //Auth in azure api
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
