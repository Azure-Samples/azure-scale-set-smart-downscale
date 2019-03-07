#!/bin/bash

# Exit if any command fails
set -e

export AZURE_RANDOM_ID=$RANDOM

# Azure Subscription ID to deploy
export AZURE_SUBSCRIPTION_ID=
# Azure Resource Group name
export AZURE_RG_NAME=smart-scale-set-$AZURE_RANDOM_ID-rg
# Azure DC Location -- assume that FunctionApp consumption plan is availible in this location
# Othewise should use specific location for FunctionApp creation
export AZURE_DC_LOCATION=southcentralus
# Azure VM Scale Set name
export AZURE_SCALESET_NAME=smart-scale-set-$AZURE_RANDOM_ID
# Azure VM Scale Set LB
export AZURE_SCALESET_LB=smart-scale-set-lb-$AZURE_RANDOM_ID

# Azure subnet ResourceID
export AZURE_SCALESET_SUBNET=

# Azure Scale Set VM URN or URI
export AZURE_SCALESET_BASE_IMAGE=UbuntuLTS
# Azure Scale Set VM SKU
export AZURE_SCALESET_VM_SKU=Standard_B1s
# Azure VM Admin User Name
export AZURE_SCALESET_VM_USER_NAME=render_user
export AZURE_SCALESET_VM_USER_PASSWORD=AlgousPass11
export AZURE_SCALESET_INSTANCE_COUNT=10

# Azure Storage Account name for the metrics collection usage
export AZURE_SA_NAME=metricsstorage$AZURE_RANDOM_ID
# Azure FunctionApp Name
export AZURE_FUNC_NAME=ScaleSetManager$AZURE_RANDOM_ID

# Azure FunctionApp Zip Package Name
export AZURE_FUNC_PACKAGE=ScaleDown.zip

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

# Create Azure Resource Group
az group create --name $AZURE_RG_NAME --location $AZURE_DC_LOCATION

# Generate SP and export my.azureauth file for the function to manage scale set
az ad sp create-for-rbac --scopes /subscriptions/$AZURE_SUBSCRIPTION_ID/resourceGroups/$AZURE_RG_NAME --sdk-auth > my.azureauth


# Create Azure VM Scale Set -- can be customized according requrements
# Currently uses just base parameters for PoC
if [ -z "$AZURE_SCALESET_SUBNET" ]
then
# $AZURE_SCALESET_SUBNET is empty
az vmss create -n $AZURE_SCALESET_NAME -g $AZURE_RG_NAME \
            --image $AZURE_SCALESET_BASE_IMAGE \
            --vm-sku $AZURE_SCALESET_VM_SKU \
            --load-balancer $AZURE_SCALESET_LB --lb-sku=Basic \
            --admin-username $AZURE_SCALESET_VM_USER_NAME \
            --admin-password $AZURE_SCALESET_VM_USER_PASSWORD \
            --instance-count $AZURE_SCALESET_INSTANCE_COUNT \
            --eviction-policy delete \
            --priority Low
else
# $AZURE_SCALESET_SUBNET is set
az vmss create -n $AZURE_SCALESET_NAME -g $AZURE_RG_NAME \
            --subnet $AZURE_SCALESET_SUBNET \
            --image $AZURE_SCALESET_BASE_IMAGE \
            --vm-sku $AZURE_SCALESET_VM_SKU \
            --load-balancer $AZURE_SCALESET_LB --lb-sku=Basic \
            --admin-username $AZURE_SCALESET_VM_USER_NAME \
            --admin-password $AZURE_SCALESET_VM_USER_PASSWORD \
            --instance-count $AZURE_SCALESET_INSTANCE_COUNT \
            --eviction-policy delete \
            --priority Low

fi

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
export AZURE_SCALESET_ID=`az vmss show --resource-group $AZURE_RG_NAME --name $AZURE_SCALESET_NAME --query id --output tsv`

# Cerate storage secret info JSON
export STORAGE_SECRET="{'storageAccountName': '$AZURE_SA_NAME', 'storageAccountSasToken': '$AZURE_SA_SAS_TOKEN'}"

# Get default config for VMSS metrics for testing purposes
# az vmss diagnostics get-default-config > default_config.json

export METRICS_FILE_NAME=metrics_config_$AZURE_RANDOM_ID.json

cp metrics_config.json $METRICS_FILE_NAME

# Replace placeholders in metrics config file with actual data
sed -i "s#__DIAGNOSTIC_STORAGE_ACCOUNT__#$AZURE_SA_NAME#g" $METRICS_FILE_NAME
sed -i "s#__VM_OR_VMSS_RESOURCE_ID__#$AZURE_SCALESET_ID#g" $METRICS_FILE_NAME

# Add metrics as sepcified in metrics_config.json to scale set
az vmss diagnostics set --resource-group $AZURE_RG_NAME \
                        --vmss-name $AZURE_SCALESET_NAME \
                        --settings  $METRICS_FILE_NAME \
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
# FunctionApp runtime for netcore app - 2 is required
FUNCTION_APP_SETTINGS=$FUNCTION_APP_SETTINGS"FUNCTIONS_EXTENSION_VERSION=~2 "
# We use zip package deployment so tell it to runtime
FUNCTION_APP_SETTINGS=$FUNCTION_APP_SETTINGS"WEBSITE_RUN_FROM_PACKAGE=1"

# Create FunctionApp
az functionapp create --name $AZURE_FUNC_NAME --resource-group $AZURE_RG_NAME  --storage-account $AZURE_SA_NAME --consumption-plan-location $AZURE_DC_LOCATION

# Add AppSettings to FunctionApp
az functionapp config appsettings set --settings $FUNCTION_APP_SETTINGS --name $AZURE_FUNC_NAME --resource-group $AZURE_RG_NAME

# Add authentication information to FunctionApp Package
zip -r $AZURE_FUNC_PACKAGE my.azureauth

# Deploy FunctionApp Package
az functionapp deployment source config-zip  --name $AZURE_FUNC_NAME --resource-group $AZURE_RG_NAME  --src $AZURE_FUNC_PACKAGE
