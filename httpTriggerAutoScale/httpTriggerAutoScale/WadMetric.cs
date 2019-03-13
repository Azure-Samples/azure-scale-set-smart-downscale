using Microsoft.Azure.Cosmos.Table;
using System;
using System.Collections.Generic;
using System.Text;

namespace httpTriggerAutoScale
{

    /// <summary>
    /// Class to store entities from azure table metrics
    /// </summary>
    class WadMetric : TableEntity
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

        public string Role { get; set; }

        public string RoleInstance { get; set; }

        public double Last { get; set; }
        public double Maximum { get; set; }
        public double Minimum { get; set; }
        public DateTime TIMESTAMP { get; set; }
        public double Total { get; set; }
    }
}
