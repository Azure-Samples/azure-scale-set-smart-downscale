//using Microsoft.Azure.Cosmos.Table;
using Azure;
using Azure.Data.Tables;
using System;
using System.Collections.Generic;
using System.Text;

namespace httpTriggerAutoScale
{

    /// <summary>
    /// Class to store entities from azure table metrics
    /// </summary>
    class WadMetric: ITableEntity
    {
        public WadMetric(string PartitionKey, string RowKey)
        {
            this.PartitionKey = PartitionKey;
            this.RowKey = RowKey;
        }
        public WadMetric() { }
        public string PartitionKey { get; set; }
        public string RowKey { get; set; }
        public double Average { get; set; }
        public int Count { get; set; }
        public string CounterName { get; set; }
        public string DeploymentId { get; set; }
        public string Host { get; set; }
        public string RoleInstance { get; set; }
        public double Last { get; set; }
        public double Maximum { get; set; }
        public double Minimum { get; set; }
        public double Total { get; set; }
        public DateTimeOffset? Timestamp { get; set; }
        public ETag ETag { get; set; }
    }
}
