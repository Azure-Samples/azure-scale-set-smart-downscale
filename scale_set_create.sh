#!/bin/bash

# Azure Subscription ID to deploy
export AZURE_SUBSCRIPTION_ID=
# Azure Resource Group name
export AZURE_RG_NAME=smart-scale-set-rg			 
# Azure DC Location
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
# Azure Function Plan (AppService)
export AZURE_FUNC_PLAN_NAME=ScaleDownPlan	
# Azure Function Plan (AppService)
export AZURE_FUNC_NAME=ScaleDown	


# Login to start script
az login

# Set required subscription
az account set -s $AZURE_SUBSCRIPTION_ID

# Create Azure Resource Group
az group create --name $AZURE_RG_NAME --location $AZURE_DC_LOCATION

# Create Azure VM Scale Set -- can be customized according requrements
# Currently uses just base parameters for PoC
az vmss create -n $AZURE_SCALESET_NAME -g $AZURE_RG_NAME \
            --image $AZURE_SCALESET_BASE_IMAGE \
            --vm-sku $AZURE_SCALESET_VM_SKU \
            --load-balancer $AZURE_SCALESET_LB --lb-sku=Basic \
            --generate-ssh-keys


# Create Azure Storage Account
az storage account create --name $AZURE_SA_NAME --location $AZURE_DC_LOCATION --resource-group $AZURE_RG_NAME --sku Standard_LRS

# Create SAS token expiry date +1 year to current datetime
# Wrong doc -- the format of datetime is '+%Y-%m-%dT%H:%M:00Z'
export AZURE_SA_SAS_EXPIRY_DATE=`date -d "1 year" '+%Y-%m-%dT%H:%M:00Z'`
export AZURE_SA_SAS_START_DATE=`date -d "-1 year" '+%Y-%m-%dT%H:%M:00Z'`

# Get SAS token
export AZURE_SA_SAS_TOKEN=`az storage account generate-sas --permissions acluw --account-name $AZURE_SA_NAME --services bt --resource-types co --expiry $AZURE_SA_SAS_EXPIRY_DATE --start $AZURE_SA_SAS_START_DATE --output tsv`

# Get Scale Set Resource ID
export AZURE_SCALESET_ID=`az vmss list --resource-group $AZURE_RG_NAME  --query [0].id --output tsv`

# Cerate storage secret info JSON
STORAGE_SECRET="{'storageAccountName': '$AZURE_SA_NAME', 'storageAccountSasToken': '$AZURE_SA_SAS_TOKEN'}"

# Get default config for VMSS metrics for testing purposes
az vmss diagnostics get-default-config > default_config.json

# Replace placeholders in metrics config file with actual data
sed -i "s#__DIAGNOSTIC_STORAGE_ACCOUNT__#$AZURE_SA_NAME#g" default_config.json
sed -i "s#__VM_OR_VMSS_RESOURCE_ID__#$AZURE_SCALESET_ID#g" default_config.json

# Add metrics as sepcified in metrics_config.json to scale set
az vmss diagnostics set --resource-group $AZURE_RG_NAME \
                        --vmss-name $AZURE_SCALESET_NAME \
                        --settings  default_config.json \
                        --protected-settings "${STORAGE_SECRET}"

export AZURE_SA_CONNECTION_STRING=`az storage account show-connection-string  --name $AZURE_SA_NAME --resource-group $AZURE_RG_NAME --output tsv`

FUNCTION_APP_SETTINGS="ScaleSetId=$AZURE_SCALESET_ID LookupTimeInMinutes=5 CPUTreshold=5 TablePrefix=WADMetricsPT1M StorageAccountConnectionString=$AZURE_SA_CONNECTION_STRING"


# Create AppService plan for Function to use Env variables to config
az appservice plan create --resource-group $AZURE_RG_NAME --location $AZURE_DC_LOCATION --name $AZURE_FUNC_PLAN_NAME

# Create FunctionApp
az functionapp create --name $AZURE_FUNC_NAME --resource-group $AZURE_RG_NAME  --storage-account $AZURE_SA_NAME --plan $AZURE_FUNC_PLAN_NAME

# Add Env Variables to config Func
az functionapp config appsettings set --settings $FUNCTION_APP_SETTINGS --name $AZURE_FUNC_NAME --resource-group $AZURE_RG_NAME