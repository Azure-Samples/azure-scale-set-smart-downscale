#!/bin/bash

# Azure Subscription ID to deploy
export AZURE_SUBSCRIPTION_ID=
# Azure Resource Group name
export AZURE_RG_NAME=smart-scale-set-rg			 
# Azure DC Location -- assume that FunctionApp consumption plan is availible in this location
# Othewise should use specific location for FunctionApp creation
export AZURE_DC_LOCATION=southcentralus	
# Azure VM Scale Set name
export AZURE_SCALESET_NAME=smart-scale-set	
# Azure VM Scale Set LB
export AZURE_SCALESET_LB=smart-scale-set-lb

# Azure Scale Set VM URN or URI
export AZURE_SCALESET_BASE_IMAGE=UbuntuLTS
# Azure Scale Set VM SKU
export AZURE_SCALESET_VM_SKU=Standard_B1s
# Azure Storage Account name for the metrics collection usage
export AZURE_SA_NAME=metricsstorageaccount	
# Azure FunctionApp Name
export AZURE_FUNC_NAME=ScaleSetManager$RANDOM

# Parameters for FunctionApp
export FUNC_PARAM_LOOKUP_TIME_IN_MINUTES=5
export FUNC_PARAM_CPU_TRESHOLD=5
export FUNC_PARAM_TABLE_PREFIX=WADMetricsPT1M
export FUNC_PARAM_STARTUP_DELAY_IN_MIN=30
export FUNC_PARAM_DISK_TRESHOLD_BYTES=3145728

# Login to start script
az login

# Set required subscription
az account set -s $AZURE_SUBSCRIPTION_ID

# Generate my.azureauth file for function
az ad sp create-for-rbac --sdk-auth > my.azureauth

# Create Azure Resource Group
az group create --name $AZURE_RG_NAME --location $AZURE_DC_LOCATION

# Create Azure VM Scale Set -- can be customized according requrements
# Currently uses just base parameters for PoC
az vmss create -n $AZURE_SCALESET_NAME -g $AZURE_RG_NAME \
            --image $AZURE_SCALESET_BASE_IMAGE \
            --vm-sku $AZURE_SCALESET_VM_SKU \
            --load-balancer $AZURE_SCALESET_LB --lb-sku=Basic \
            --generate-ssh-keys

 export FUNC_PARAM_TIME_OF_CREATION=`date -u '+%Y-%m-%dT%H:%M:00Z'`
 FUNC_PARAM_TIME_OF_CREATION=`echo -n $FUNC_PARAM_TIME_OF_CREATION|base64 --wrap=0`

# Create Azure Storage Account
az storage account create --name $AZURE_SA_NAME --location $AZURE_DC_LOCATION --resource-group $AZURE_RG_NAME --sku Standard_LRS

# Create SAS token expiry date +1 year to current datetime
# Wrong doc -- the format of datetime is '+%Y-%m-%dT%H:%M:00Z'
export AZURE_SA_SAS_EXPIRY_DATE=`date -u -d "1 year" '+%Y-%m-%dT%H:%M:00Z'`
export AZURE_SA_SAS_START_DATE=`date -u -d "-1 year" '+%Y-%m-%dT%H:%M:00Z'`

# Get SAS token
export AZURE_SA_SAS_TOKEN=`az storage account generate-sas --permissions acluw --account-name $AZURE_SA_NAME --services bt --resource-types co --expiry $AZURE_SA_SAS_EXPIRY_DATE --start $AZURE_SA_SAS_START_DATE --output tsv`

# Get Scale Set Resource ID
export AZURE_SCALESET_ID=`az vmss list --resource-group $AZURE_RG_NAME  --query [0].id --output tsv`

# Cerate storage secret info JSON
export STORAGE_SECRET="{'storageAccountName': '$AZURE_SA_NAME', 'storageAccountSasToken': '$AZURE_SA_SAS_TOKEN'}"

# Get default config for VMSS metrics for testing purposes
# az vmss diagnostics get-default-config > default_config.json

# Replace placeholders in metrics config file with actual data
sed -i "s#__DIAGNOSTIC_STORAGE_ACCOUNT__#$AZURE_SA_NAME#g" metrics_config.json
sed -i "s#__VM_OR_VMSS_RESOURCE_ID__#$AZURE_SCALESET_ID#g" metrics_config.json

# Add metrics as sepcified in metrics_config.json to scale set
az vmss diagnostics set --resource-group $AZURE_RG_NAME \
                        --vmss-name $AZURE_SCALESET_NAME \
                        --settings  metrics_config.json \
                        --protected-settings "${STORAGE_SECRET}"



# Get Azure Storage Account connection string to use in Fucntion App
export AZURE_SA_CONNECTION_STRING=`az storage account show-connection-string  --name $AZURE_SA_NAME --resource-group $AZURE_RG_NAME --output tsv`

# Build AppSettins for Function App
# Space separated <key>=<value> pairs
# To simplify management devided for series of operations
export FUNCTION_APP_SETTINGS="ScaleSetId=$AZURE_SCALESET_ID "
FUNCTION_APP_SETTINGS=$FUNCTION_APP_SETTINGS"LookupTimeInMin=$FUNC_PARAM_LOOKUP_TIME_IN_MINUTES "
FUNCTION_APP_SETTINGS=$FUNCTION_APP_SETTINGS"CPUTresholdInPercent=$FUNC_PARAM_CPU_TRESHOLD "
FUNCTION_APP_SETTINGS=$FUNCTION_APP_SETTINGS"TablePrefix=$FUNC_PARAM_TABLE_PREFIX "
FUNCTION_APP_SETTINGS=$FUNCTION_APP_SETTINGS"DiskTresholdBytes=$FUNC_PARAM_DISK_TRESHOLD_BYTES "
FUNCTION_APP_SETTINGS=$FUNCTION_APP_SETTINGS"StorageAccountConnectionString=$AZURE_SA_CONNECTION_STRING "
FUNCTION_APP_SETTINGS=$FUNCTION_APP_SETTINGS"StartupDelayInMin=$FUNC_PARAM_STARTUP_DELAY_IN_MIN "
FUNCTION_APP_SETTINGS=$FUNCTION_APP_SETTINGS"TimeOfCreation=$FUNC_PARAM_TIME_OF_CREATION "
# FunctionApp runtime for netcore app 2 is required
FUNCTION_APP_SETTINGS=$FUNCTION_APP_SETTINGS"FUNCTIONS_EXTENSION_VERSION=~2 "
# We use zip package deployment so tell it to runtime
FUNCTION_APP_SETTINGS=$FUNCTION_APP_SETTINGS"WEBSITE_RUN_FROM_PACKAGE=1"

# Create FunctionApp
az functionapp create --name $AZURE_FUNC_NAME --resource-group $AZURE_RG_NAME  --storage-account $AZURE_SA_NAME --consumption-plan-location $AZURE_DC_LOCATION

# Add AppSettings to FunctionApp
az functionapp config appsettings set --settings $FUNCTION_APP_SETTINGS --name $AZURE_FUNC_NAME --resource-group $AZURE_RG_NAME

# Add authentication information to FunctionApp Package
zip -r ScaleDown.zip my.azureauth

# Deploy FunctionApp Package
az functionapp deployment source config-zip  --name $AZURE_FUNC_NAME --resource-group $AZURE_RG_NAME  --src ScaleDown.zip