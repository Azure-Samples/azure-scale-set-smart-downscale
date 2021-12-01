using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using System;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;

namespace httpTriggerAutoScale
{
    public static class HttpScaleDown
    {
        //!!!!!!!!!!!!!!Important note!!!!!!!!!!!!!
        //Before you will start, you will need a file my.azureauth with Rbac credentials here is a command to generate it
        //az ad sp create-for-rbac --sdk-auth > my.azureauth
        //!!!!!!!!!!!!!!!!!!!!!!!!!!!      

        [FunctionName("HttpScaleDown")]
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
            int MinNumNodes = int.Parse(Environment.GetEnvironmentVariable("MinNumNodes"));
            string TablePrefix = Environment.GetEnvironmentVariable("TablePrefix");
            string StorageAccountConnectionString = Environment.GetEnvironmentVariable("StorageAccountConnectionString");

            //We need store datetime value in base64 because azure function host automatially convert it to short date/time format
            //without time zone info.
            bool parseddate = DateTime.TryParseExact(
                Encoding.UTF8.GetString(
                    Convert.FromBase64String(
                        Environment.GetEnvironmentVariable("TimeOfCreation"))), "yyyy-MM-ddTHH:mm:ssZ", provider,DateTimeStyles.AdjustToUniversal, out TimeOfCreation);
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

                ScaleSetManager manager = new ScaleSetManager(ScaleSetId, context, log);

                var metrics = manager.GetMetricsFromTable(StorageAccountConnectionString, LookupTimeInMinutes, TablePrefix);

                var instances = manager.GetInstancesToKill(metrics, CPUTreshold, DiskTresholdBytes);

                var dealocated = manager.DealocateInstances(instances, MinNumNodes);

                logString = $"Done, number of dealocated instances {dealocated.Count.ToString()}: {String.Join(", ", dealocated.ToArray())}";
                log.LogInformation(logString);
            }
            else {

                logString = $"HTTP trigger function processed but Its to early to scale cluster time not come.";
                log.LogInformation(logString);
            }

            return (ActionResult)new OkObjectResult(logString);         
        }
       
    } 
}
