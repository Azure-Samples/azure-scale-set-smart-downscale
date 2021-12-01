using Azure.Data.Tables;
using Microsoft.Azure.Management.Compute.Fluent;
using Microsoft.Azure.Management.Compute.Fluent.Models;
using Microsoft.Azure.Management.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent.Core;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace httpTriggerAutoScale
{
    internal class ScaleSetManager
    {
        public ScaleSetManager(string scalesetId, ExecutionContext context, ILogger Log) {

            log = Log;
            AzureInstance = AzureAuth(context);
            AutoSwitchOSMetrics(scalesetId, AzureInstance);

            scalesetid = scalesetId;
        }

        ILogger log;
        IAzure AzureInstance;
        string scalesetid;

        OperatingSystemTypes clusterOSType = OperatingSystemTypes.Linux;
        public OperatingSystemTypes ClusterOSType
        {
            get { return clusterOSType; }
            private set { clusterOSType = value; }

        }

        //Default metrics is set to Linux
        string CpuMetricName = "/builtin/processor/percentprocessortime";
        string DiskMetricName = "/builtin/disk/bytespersecond";

        const string CpuMetricNameWindows = @"\Processor(_Total)\% Processor Time";
        const string DiskMetricNameWindows = @"\PhysicalDisk(_Total)\Disk Read Bytes/sec";

        /// <summary>
        /// Dealocating instances in scale-set based on instances list of ids
        /// </summary>
        internal List<string> DealocateInstances(List<string> InstancesToKill, int MinNumNodes)
        {

            var scaleset = AzureInstance.VirtualMachineScaleSets.GetById(scalesetid);

            var instances = scaleset.VirtualMachines.List();

            //make sure we keeping min num nodes in cluster
            var diff = instances.Count() - InstancesToKill.Count();
            if (diff < MinNumNodes)
            {
                InstancesToKill.RemoveRange(0, MinNumNodes - diff);
            }

            List<string> dealocatedInstances = new List<string>();

            List<Task> TaskList = new List<Task>();

            foreach (var ins in instances)
            {
                if (InstancesToKill.Contains(ClusterOSType == OperatingSystemTypes.Linux ? ins.ComputerName : "_" + ins.Name))
                {
                    try
                    {
                        //Check if instances not deallocated before or in dealocatting state
                        if (ins.PowerState != PowerState.Deallocated && ins.PowerState != PowerState.Deallocating && ins.PowerState != PowerState.Starting)
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
        internal List<string> GetInstancesToKill(List<WadMetric> Metrics, int CPUTreshold, int DiskTreshold)
        {
            List<string> instances = new List<string>();

            if (Metrics != null)
            {
                if (Metrics.Count > 0)
                {
                    //Calculate average by metric grouped by Host
                    var groupedResult = Metrics.GroupBy(t => new {
                        Host = ClusterOSType == OperatingSystemTypes.Linux ? t.Host : t.RoleInstance, Metric = t.CounterName })
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
        internal List<WadMetric> GetMetricsFromTable(string StorageAccountConnectionString, int LookupTimeInMinutes, string TablePrefix)
        {

            List<WadMetric> resultList = new List<WadMetric>();

            var serviceClient = new TableServiceClient(StorageAccountConnectionString);
            var table = serviceClient.GetTableClient(TablePrefix);

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
            var result = table.Query<WadMetric>(wad => wad.PartitionKey == scalesetid.Replace("/", ":002F").Replace("-", ":002D").Replace(".", ":002E") &&
                wad.Timestamp >= qdate && (wad.CounterName == CpuMetricName || wad.CounterName == DiskMetricName));

            foreach (var entity in result)
            {
                resultList.Add(entity);
            }

            return resultList;
        }

        /// <summary>
        /// Auth in azure api
        /// </summary>
        private static IAzure AzureAuth(ExecutionContext context)
        {

            var path = System.IO.Path.Combine(context.FunctionAppDirectory, "my.azureauth");
            var credentials = SdkContext.AzureCredentialsFactory.FromFile(path);
            var azure = Microsoft.Azure.Management.Fluent.Azure
                .Configure()
                .WithLogLevel(HttpLoggingDelegatingHandler.Level.Basic)
                .Authenticate(credentials)
                .WithDefaultSubscription();

            return azure;
        }

        /// <summary>
        /// Clearing ScaleSet from stopped VMS, that can happen if there is no available cores and in other situations.
        /// </summary>
        /// <returns>Number of deleted stooped VMs</returns>
        internal int ClearStoppedVMs() {

            int deleted = 0;
            var scaleset = AzureInstance.VirtualMachineScaleSets.GetById(scalesetid);
            var instances = scaleset.VirtualMachines.List();

            List<Task> TaskList = new List<Task>();
            var stopped = instances.Where(x => x.PowerState == PowerState.Stopped || x.PowerState == PowerState.Deallocated);
            foreach (var instance in stopped) {
                TaskList.Add(instance.DeleteAsync());
                deleted++;
            }

            Task.WaitAll(TaskList.ToArray());

            return deleted;
        }

        /// <summary>
        /// Switching between Linux and Windows OSes cause they have diffrent metric names
        /// </summary>
        /// <param name="ScaleSetId"></param>
        /// <param name="AzureInstance"></param>
        private void AutoSwitchOSMetrics(string ScaleSetId, IAzure AzureInstance)
        {
            var scaleset = AzureInstance.VirtualMachineScaleSets.GetById(ScaleSetId);

            //We should check OS type on instance level cause on ScaleSet level there is an issue with OSType param
            var list = scaleset.VirtualMachines.List();
            if (list.Count() > 0)
            {
                if (list.FirstOrDefault().OSType == OperatingSystemTypes.Windows)
                {
                    ClusterOSType = OperatingSystemTypes.Windows;
                    CpuMetricName = CpuMetricNameWindows;
                    DiskMetricName = DiskMetricNameWindows;
                }
            }
        }
        
    }
}
