using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;
using System;
using System.Globalization;
using System.Text;

namespace httpTriggerAutoScale
{
    public static class TimerClearStoppedVms
    {
        [FunctionName("TimerClearStoppedVms")]
        public static void Run([TimerTrigger("0 */5 * * * *")]TimerInfo myTimer, ILogger log, ExecutionContext context)
        {
            string logString;

            #region Capture init strings

            DateTime TimeOfCreation;
            CultureInfo provider = CultureInfo.InvariantCulture;
            string ScaleSetId = Environment.GetEnvironmentVariable("ScaleSetId");
            int LookupTimeInMinutes = int.Parse(Environment.GetEnvironmentVariable("LookupTimeInMin"));
            int CPUTreshold = int.Parse(Environment.GetEnvironmentVariable("CPUTresholdInPercent"));
            int DiskTresholdBytes = int.Parse(Environment.GetEnvironmentVariable("DiskTresholdBytes"));
            int MinNumNodes = int.Parse(Environment.GetEnvironmentVariable("MinNumNodes"));
            string TablePrefix = Environment.GetEnvironmentVariable("TablePrefix");
            string StorageAccountConnectionString = Environment.GetEnvironmentVariable("StorageAccountConnectionString");

            //We need store datetime value in base64 because azure function host automatially convert it to short date/time format
            //without time zone info.
            bool parseddate = DateTime.TryParseExact(
                Encoding.UTF8.GetString(
                    Convert.FromBase64String(
                        Environment.GetEnvironmentVariable("TimeOfCreation"))), "yyyy-MM-ddTHH:mm:ssZ", provider, DateTimeStyles.AdjustToUniversal, out TimeOfCreation);
            TimeSpan StartupDelayInMin = TimeSpan.FromMinutes(double.Parse(Environment.GetEnvironmentVariable("StartupDelayInMin")));
            var timeInPast = DateTime.UtcNow.Subtract(StartupDelayInMin);

            if (String.IsNullOrEmpty(ScaleSetId) || LookupTimeInMinutes <= 0 || CPUTreshold <= 0 || DiskTresholdBytes <= 0 ||
                String.IsNullOrEmpty(TablePrefix) || String.IsNullOrEmpty(StorageAccountConnectionString) || !parseddate)
            {
                logString = $"HTTP trigger function started but not all init params are set";
                log.LogInformation(logString);
            }
            #endregion

            //Clearing stopped VMs
            ScaleSetManager manager = new ScaleSetManager(ScaleSetId, context, log);
            var result = manager.ClearStoppedVMs();

            log.LogInformation("Deleted stopped or dellocated VMs:"+result.ToString());

        }
    }
}
