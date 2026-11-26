param(
    [string]$ResourceGroup = "lore-agy-dev",
    [string]$Location = "uksouth",
    [string]$Suffix = "",
    [string]$CosmosDatabase = "lore",
    [string]$EmbeddingDeployment = "text-embedding-3-large",
    [string]$AnthropicModel = "claude-haiku-5-5",
    [string]$Image = "",
    [switch]$ImportHindsightAnthropicKey,
    [switch]$DeployApi
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($Suffix)) {
    $subId = (az account show --query id -o tsv).Trim()
    $bytes = [System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes("$subId-$ResourceGroup"))
    $Suffix = ([BitConverter]::ToString($bytes).Replace("-", "").ToLowerInvariant()).Substring(0, 6)
}

$CosmosAccount = "lore-agy-dev-$Suffix"
$AoaiAccount = "lore-agy-aoai-$Suffix"

Write-Host "Using resource group '$ResourceGroup' in '$Location' (suffix: $Suffix)"

$principalId = (az ad signed-in-user show --query id -o tsv).Trim()
if ([string]::IsNullOrWhiteSpace($principalId)) {
    throw "Could not resolve signed-in Entra user ID. Run 'az login' first."
}

Write-Host "Ensuring resource group '$ResourceGroup'..."
az group create --name $ResourceGroup --location $Location --only-show-errors -o none

$existingCosmos = az cosmosdb show --name $CosmosAccount --resource-group $ResourceGroup --query name -o tsv 2>$null
if ([string]::IsNullOrWhiteSpace($existingCosmos)) {
    Write-Host "Creating Cosmos DB Serverless account '$CosmosAccount' with Vector + FullText search..."
    az cosmosdb create `
        --name $CosmosAccount `
        --resource-group $ResourceGroup `
        --locations regionName=$Location failoverPriority=0 isZoneRedundant=False `
        --capabilities EnableServerless EnableNoSQLVectorSearch EnableNoSQLFullTextSearch `
        --default-consistency-level Session `
        --only-show-errors -o none
} else {
    Write-Host "Cosmos DB account '$CosmosAccount' already exists."
}

$cosmosEndpoint = (az cosmosdb show --name $CosmosAccount --resource-group $ResourceGroup --query documentEndpoint -o tsv).Trim()
$cosmosArmId = (az cosmosdb show --name $CosmosAccount --resource-group $ResourceGroup --query id -o tsv).Trim()
$cosmosApiVersion = "2024-11-15"

Write-Host "Ensuring Cosmos DB database '$CosmosDatabase' and containers (memories, chunks with DiskANN+FullText, telemetry)..."
az cosmosdb sql database create `
    --account-name $CosmosAccount `
    --resource-group $ResourceGroup `
    --name $CosmosDatabase `
    --only-show-errors -o none

az cosmosdb sql container create `
    --account-name $CosmosAccount `
    --resource-group $ResourceGroup `
    --database-name $CosmosDatabase `
    --name "memories" `
    --partition-key-path "/repoId" `
    --only-show-errors -o none

$chunksBody = @{
    properties = @{
        resource = @{
            id           = "chunks"
            partitionKey = @{
                paths = @("/repoId")
                kind  = "Hash"
            }
            vectorEmbeddingPolicy = @{
                vectorEmbeddings = @(
                    @{
                        path             = "/embedding"
                        dataType         = "float32"
                        distanceFunction = "cosine"
                        dimensions       = 1536
                    }
                )
            }
            fullTextPolicy = @{
                defaultLanguage = "en-US"
                fullTextPaths   = @(
                    @{
                        path     = "/searchText"
                        language = "en-US"
                    }
                )
            }
            indexingPolicy = @{
                automatic     = $true
                indexingMode  = "consistent"
                includedPaths = @(@{ path = "/*" })
                excludedPaths = @(
                    @{ path = '/"_etag"/?' },
                    @{ path = "/embedding/*" }
                )
                vectorIndexes = @(
                    @{
                        path = "/embedding"
                        type = "diskANN"
                    }
                )
                fullTextIndexes = @(
                    @{
                        path = "/searchText"
                    }
                )
            }
        }
    }
} | ConvertTo-Json -Depth 12 -Compress

$chunksBodyFile = [System.IO.Path]::GetTempFileName()
Set-Content -Path $chunksBodyFile -Value $chunksBody -Encoding utf8
try {
    az rest --method PUT --uri "https://management.azure.com$cosmosArmId/sqlDatabases/$CosmosDatabase/containers/chunks?api-version=$cosmosApiVersion" --headers "Content-Type=application/json" --body "@$chunksBodyFile" --only-show-errors -o none
} finally {
    Remove-Item $chunksBodyFile -ErrorAction SilentlyContinue
}

az cosmosdb sql container create `
    --account-name $CosmosAccount `
    --resource-group $ResourceGroup `
    --database-name $CosmosDatabase `
    --name "telemetry" `
    --partition-key-path "/repoId" `
    --ttl 7776000 `
    --only-show-errors -o none

Write-Host "Ensuring Cosmos DB SQL Built-in Data Contributor RBAC assignment..."
$existingCosmosRole = az cosmosdb sql role assignment list `
    --account-name $CosmosAccount `
    --resource-group $ResourceGroup `
    --query "[?principalId=='$principalId' && contains(roleDefinitionId, '00000000-0000-0000-0000-000000000002')].id" -o tsv
if ([string]::IsNullOrWhiteSpace($existingCosmosRole)) {
    az cosmosdb sql role assignment create `
        --account-name $CosmosAccount `
        --resource-group $ResourceGroup `
        --role-definition-id "00000000-0000-0000-0000-000000000002" `
        --principal-id $principalId `
        --scope "/" `
        --only-show-errors -o none
}

$existingAoai = az cognitiveservices account show --name $AoaiAccount --resource-group $ResourceGroup --query name -o tsv 2>$null
if ([string]::IsNullOrWhiteSpace($existingAoai)) {
    Write-Host "Creating Azure OpenAI account '$AoaiAccount'..."
    az cognitiveservices account create `
        --name $AoaiAccount `
        --resource-group $ResourceGroup `
        --location $Location `
        --kind OpenAI `
        --sku S0 `
        --custom-domain $AoaiAccount `
        --only-show-errors -o none
} else {
    Write-Host "Azure OpenAI account '$AoaiAccount' already exists."
}

$aoaiId = (az cognitiveservices account show --name $AoaiAccount --resource-group $ResourceGroup --query id -o tsv).Trim()
$aoaiEndpoint = (az cognitiveservices account show --name $AoaiAccount --resource-group $ResourceGroup --query properties.endpoint -o tsv).Trim()

$existingDeploy = az cognitiveservices account deployment show `
    --name $AoaiAccount `
    --resource-group $ResourceGroup `
    --deployment-name $EmbeddingDeployment `
    --query name -o tsv 2>$null
if ([string]::IsNullOrWhiteSpace($existingDeploy)) {
    Write-Host "Deploying '$EmbeddingDeployment' (GlobalStandard)..."
    az cognitiveservices account deployment create `
        --name $AoaiAccount `
        --resource-group $ResourceGroup `
        --deployment-name $EmbeddingDeployment `
        --model-name $EmbeddingDeployment `
        --model-version "1" `
        --model-format OpenAI `
        --sku-capacity 10 `
        --sku-name GlobalStandard `
        --only-show-errors -o none
} else {
    Write-Host "Deployment '$EmbeddingDeployment' already exists."
}

Write-Host "Ensuring 'Cognitive Services OpenAI User' RBAC assignment..."
$existingAoaiRole = az role assignment list `
    --assignee $principalId `
    --scope $aoaiId `
    --role "Cognitive Services OpenAI User" `
    --query "[0].id" -o tsv
if ([string]::IsNullOrWhiteSpace($existingAoaiRole)) {
    az role assignment create `
        --assignee-object-id $principalId `
        --assignee-principal-type User `
        --role "Cognitive Services OpenAI User" `
        --scope $aoaiId `
        --only-show-errors -o none
}

$loreHome = if ($env:LORE_HOME) { $env:LORE_HOME } else { Join-Path $env:USERPROFILE ".lore" }
New-Item -ItemType Directory -Path $loreHome -Force | Out-Null

$configPath = Join-Path $loreHome "config.json"
$configObj = [ordered]@{
    cosmosEndpoint                 = $cosmosEndpoint
    cosmosDatabase                 = $CosmosDatabase
    azureOpenAiEndpoint            = $aoaiEndpoint
    azureOpenAiEmbeddingDeployment = $EmbeddingDeployment
    embeddingDimensions            = 1536
    anthropicModel                 = $AnthropicModel
    promptRecallMinScore           = 0.45
    hookTimeoutMs                  = 10000
    bootstrapCacheMinutes          = 5
}
$configObj | ConvertTo-Json -Depth 4 | Set-Content -Path $configPath -Encoding utf8
Write-Host "Wrote config to $configPath"

if ($ImportHindsightAnthropicKey) {
    $secretsPath = Join-Path $loreHome "secrets.json"
    $envLines = wsl -d Ubuntu-26.04 -- docker inspect --format "{{range .Config.Env}}{{println .}}{{end}}" hindsight-personal 2>$null
    $keyLine = $envLines | Where-Object { $_ -like "HINDSIGHT_API_LLM_API_KEY=*" } | Select-Object -First 1
    if ($keyLine) {
        $extractedKey = ($keyLine -split "=", 2)[1].Trim()
        if (-not [string]::IsNullOrWhiteSpace($extractedKey)) {
            $secretsObj = @{}
            if (Test-Path $secretsPath) {
                try {
                    $existing = Get-Content $secretsPath -Raw | ConvertFrom-Json -AsHashtable
                    if ($existing) { $secretsObj = $existing }
                } catch {}
            }
            $secretsObj["anthropicApiKey"] = $extractedKey
            $secretsObj | ConvertTo-Json -Depth 4 | Set-Content -Path $secretsPath -Encoding utf8
            Write-Host "Imported Anthropic API key into $secretsPath (value redacted)."
        }
    } else {
        Write-Warning "Could not locate HINDSIGHT_API_LLM_API_KEY on hindsight-personal."
    }
}

if ($DeployApi) {
    $envName = "lore-agy-env-$Suffix"
    $appName = "lore-agy-api"

    $hasExt = az extension show --name containerapp --query name -o tsv 2>$null
    if ([string]::IsNullOrWhiteSpace($hasExt)) {
        Write-Host "Installing Azure CLI 'containerapp' extension..."
        az extension add --name containerapp --upgrade --only-show-errors -o none
    }

    $secretsPath = Join-Path $loreHome "secrets.json"
    $secretsObj = @{}
    if (Test-Path $secretsPath) {
        try {
            $existing = Get-Content $secretsPath -Raw | ConvertFrom-Json -AsHashtable
            if ($existing) { $secretsObj = $existing }
        } catch {}
    }

    if (-not $secretsObj.ContainsKey("apiKey") -or [string]::IsNullOrWhiteSpace($secretsObj["apiKey"])) {
        $randBytes = New-Object byte[] 32
        [System.Security.Cryptography.RandomNumberGenerator]::Fill($randBytes)
        $secretsObj["apiKey"] = ([BitConverter]::ToString($randBytes).Replace("-", "").ToLowerInvariant())
        $secretsObj | ConvertTo-Json -Depth 4 | Set-Content -Path $secretsPath -Encoding utf8
        Write-Host "Generated API bearer key in $secretsPath (value redacted)."
    }

    $loreApiKey = $secretsObj["apiKey"]
    $anthropicKey = $secretsObj["anthropicApiKey"]

    Write-Host "Ensuring Container Apps environment '$envName'..."
    $existingEnv = az containerapp env show --name $envName --resource-group $ResourceGroup --query name -o tsv 2>$null
    if ([string]::IsNullOrWhiteSpace($existingEnv)) {
        az containerapp env create --name $envName --resource-group $ResourceGroup --location $Location --only-show-errors -o none
    }

    $registryArgs = @()
    if ([string]::IsNullOrWhiteSpace($Image)) {
        $acrName = "loreagyacr$Suffix"
        Write-Host "Provisioning Container Registry '$acrName' and building image..."
        $existingAcr = az acr show --name $acrName --resource-group $ResourceGroup --query name -o tsv 2>$null
        if ([string]::IsNullOrWhiteSpace($existingAcr)) {
            az acr create --name $acrName --resource-group $ResourceGroup --location $Location --sku Basic --admin-enabled false --only-show-errors -o none
        }
        $repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
        az acr build --registry $acrName --image "lore-api:latest" --file (Join-Path $repoRoot "src/Lore.Api/Dockerfile") $repoRoot --only-show-errors -o none
        $acrServer = (az acr show --name $acrName --resource-group $ResourceGroup --query loginServer -o tsv).Trim()
        $Image = "$acrServer/lore-api:latest"
        $registryArgs = @("--registry-server", $acrServer, "--registry-identity", "system")
    } else {
        Write-Host "Using external container image '$Image' (skipping ACR)..."
    }

    $secretArgs = @("lore-api-key=$loreApiKey")
    $envVarArgs = @(
        "LORE_COSMOS_ENDPOINT=$cosmosEndpoint",
        "LORE_COSMOS_DATABASE=$CosmosDatabase",
        "LORE_AOAI_ENDPOINT=$aoaiEndpoint",
        "LORE_AOAI_DEPLOYMENT=$EmbeddingDeployment",
        "LORE_ANTHROPIC_MODEL=$AnthropicModel",
        "LORE_API_KEY=secretref:lore-api-key"
    )
    if (-not [string]::IsNullOrWhiteSpace($anthropicKey)) {
        $secretArgs += "anthropic-api-key=$anthropicKey"
        $envVarArgs += "ANTHROPIC_API_KEY=secretref:anthropic-api-key"
    }

    $existingApp = az containerapp show --name $appName --resource-group $ResourceGroup --query name -o tsv 2>$null
    if ([string]::IsNullOrWhiteSpace($existingApp)) {
        Write-Host "Creating Container App '$appName'..."
        az containerapp create `
            --name $appName `
            --resource-group $ResourceGroup `
            --environment $envName `
            --image $Image `
            --cpu 0.25 `
            --memory 0.5Gi `
            --target-port 8080 `
            --ingress external `
            --min-replicas 0 `
            --max-replicas 2 `
            --system-assigned `
            @registryArgs `
            --secrets @secretArgs `
            --env-vars @envVarArgs `
            --only-show-errors -o none
    } else {
        Write-Host "Updating existing Container App '$appName'..."
        az containerapp secret set --name $appName --resource-group $ResourceGroup --secrets @secretArgs --only-show-errors -o none
        az containerapp update `
            --name $appName `
            --resource-group $ResourceGroup `
            --image $Image `
            --cpu 0.25 `
            --memory 0.5Gi `
            --min-replicas 0 `
            --max-replicas 2 `
            --set-env-vars @envVarArgs `
            --only-show-errors -o none
    }

    $appPrincipal = (az containerapp show --name $appName --resource-group $ResourceGroup --query identity.principalId -o tsv).Trim()
    $existingAppCosmosRole = az cosmosdb sql role assignment list `
        --account-name $CosmosAccount `
        --resource-group $ResourceGroup `
        --query "[?principalId=='$appPrincipal' && contains(roleDefinitionId, '00000000-0000-0000-0000-000000000002')].id" -o tsv
    if ([string]::IsNullOrWhiteSpace($existingAppCosmosRole)) {
        az cosmosdb sql role assignment create `
            --account-name $CosmosAccount `
            --resource-group $ResourceGroup `
            --role-definition-id "00000000-0000-0000-0000-000000000002" `
            --principal-id $appPrincipal `
            --scope "/" `
            --only-show-errors -o none
    }

    $existingAppAoaiRole = az role assignment list `
        --assignee $appPrincipal `
        --scope $aoaiId `
        --role "Cognitive Services OpenAI User" `
        --query "[0].id" -o tsv
    if ([string]::IsNullOrWhiteSpace($existingAppAoaiRole)) {
        az role assignment create `
            --assignee-object-id $appPrincipal `
            --assignee-principal-type ServicePrincipal `
            --role "Cognitive Services OpenAI User" `
            --scope $aoaiId `
            --only-show-errors -o none
    }

    $fqdn = (az containerapp show --name $appName --resource-group $ResourceGroup --query properties.configuration.ingress.fqdn -o tsv).Trim()
    $apiUrl = "https://$fqdn"
    Write-Host "Deployed Lore.Api at $apiUrl"

    $cfgJson = Get-Content $configPath -Raw | ConvertFrom-Json -AsHashtable
    $cfgJson["apiUrl"] = $apiUrl
    $cfgJson | ConvertTo-Json -Depth 4 | Set-Content -Path $configPath -Encoding utf8
    Write-Host "Updated $configPath with apiUrl=$apiUrl"
}

Write-Host "Azure setup complete."
