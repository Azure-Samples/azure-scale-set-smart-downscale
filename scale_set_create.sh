# Azure Resource Group name
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
az storage account create -n $AZURE_SA_NAME -l $AZURE_DC_LOCATION -g $AZURE_RG_NAME --sku Standard_LRS

# Create SAS token expiry date +1 year to current datetime
export AZURE_SA_SAS_EXPIRY_DATE=`date -d "1 year" '+%Y-%m-%dT%H:%MZ'`
# Get Azure SA Key
export AZURE_SA_KEY=`az storage account keys list -n $AZURE_SA_NAME -g $AZURE_RG_NAME --query [0].value --output tsv`
# Get SAS token
export AZURE_SA_SAS_TOKEN=`az storage account generate-sas --permissions cdlruwap --account-name $AZURE_SA_NAME --account-key $AZURE_SA_KEY --services bfqt --resource-types sco --expiry $AZURE_SA_SAS_EXPIRY_DATE --output tsv`

export AZURE_SCALESET_ID=`az vmss list --resource-group $AZURE_RG_NAME  --query [0].id --output tsv`


# Replace placeholders with actual values in secret files
sed -i "s/StorageName/$AZURE_SA_NAME/g" storage_secret.json
#sed -i 's@StorageSASToken@'$AZURE_SA_SAS_TOKEN'@' storage_secret.json
sed -i "s/StorageSASToken/$(echo $AZURE_SA_SAS_TOKEN | sed -e 's/\\/\\\\/g; s/\//\\\//g; s/&/\\\&/g')/g" storage_secret.json

sed -i "s/TRStorageName/$AZURE_SA_NAME/g" metrics_config.json
sed -i 's@TRResourceID@'$AZURE_SCALESET_ID'@' metrics_config.json


# Add metrics as sepcified in metrics_config.json to scale set
az vmss diagnostics set --resource-group $AZURE_RG_NAME \
                        --vmss-name $AZURE_SCALESET_NAME \
                        --settings  metrics_config.json \
                        --protected-settings storage_secret.json

