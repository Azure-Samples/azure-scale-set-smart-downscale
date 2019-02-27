# Azure Resource Group name
export AZURE_SUBSCRIPTION_ID=
# Azure Resource Group name
export AZURE_RG_NAME=smart-scale-set-rg			 
# Azure DC Location
export AZURE_DC_LOCATION=southcentralus	
# Azure VM Scale Set name
export AZURE_SCALESET_NAME=smart-scale-set	
# Azure VM Scale Set LB
export AZURE_SCALESET_NAME=smart-scale-set-lb

# Azure Scale Set VM URN or URI
export AZURE_SCALESET_BASE_IMAGE=UbuntuLTS
# Azure Scale Set VM SKU
export AZURE_SCALESET_VM_SKU=Standard_B1s
# Azure Storage Account name for the metrics collection usage
export AZURE_SA_NAME=metricsstorageaccount	


# Set required subscription
az account set -s $AZURE_SUBSCRIPTION_ID

# Create Azure Resource Group
az group create --name $AZURE_RG_NAME --location $AZURE_DC_LOCATION

# Create Azure VM Scale Set -- can be customized according requrements
# Currently uses just base parameters for PoC
az vmss create -n $AZURE_SCALESET_NAME -g $AZURE_RG_NAME \
            --image $AZURE_SCALESET_BASE_IMAGE \
            --vm-sku $AZURE_SCALESET_VM_SKU \
            --generate-ssh-keys


# Create Azure Storage Account
az storage account create -n $AZURE_SA_NAME -l $AZURE_DC_LOCATION -g $AZURE_RG_NAME --sku Standard_LRS

# Create SAS token expiry date +1 year to current datetime
export AZURE_SA_SAS_EXPIRY_DATE=`date -d "1 year" '+%Y-%m-%dT%H:%MZ'`
# Get SAS token
export AZURE_SA_SAS_TOKEN=`az storage account generate-sas --permissions cdlruwap --account-name $AZURE_SA_NAME --services t --resource-types sco --expiry $AZURE_SA_SAS_EXPIRY_DATE`