param(
    [Parameter(Mandatory=$true)][ValidateSet('baseline','prompted','reranked')][string]$Profile,
    [double]$MinimumSimilarity = 0.25,
    [ValidateSet('','true','false')][string]$RequireLead = '',
    [ValidateSet('','true','false')][string]$DiversifySources = '',
    [double]$RerankMinimumRelevance = 0.65,
    [int]$RerankCandidates = 20,
    [int]$RerankTimeoutSeconds = 45
)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$culture = [Globalization.CultureInfo]::InvariantCulture
$settings = @{
    V2_SEARCH_PROFILE = $Profile
    V2_SEARCH_MIN_SEMANTIC_SIMILARITY = $MinimumSimilarity.ToString($culture)
    V2_SEARCH_MIN_SEMANTIC_LEAD = '0.04'
    V2_SEARCH_REQUIRE_SEMANTIC_LEAD = $RequireLead
    V2_SEARCH_DIVERSIFY_SEMANTIC_SOURCES = $DiversifySources
    V2_SEARCH_MAX_SEMANTIC_RESULTS = '5'
    V2_SEARCH_RERANK_MIN_SIMILARITY = '0.20'
    V2_SEARCH_RERANK_MIN_RELEVANCE = $RerankMinimumRelevance.ToString($culture)
    V2_SEARCH_RERANK_CANDIDATES = $RerankCandidates.ToString($culture)
    V2_SEARCH_RERANK_TIMEOUT_SECONDS = $RerankTimeoutSeconds.ToString($culture)
}
$previous = @{}
Push-Location $repoRoot
try {
    foreach ($key in $settings.Keys) {
        $previous[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
        [Environment]::SetEnvironmentVariable($key, $settings[$key], 'Process')
    }
    docker compose up -d --no-build --no-deps app
    if ($LASTEXITCODE -ne 0) { throw 'Profile deployment failed' }
    $info = docker inspect personal-os-v2-app-1 | ConvertFrom-Json
    $modelLine = $info[0].Config.Env | Where-Object { $_ -like 'OLLAMA_EMBED_MODEL=*' }
    $model = $modelLine.Substring('OLLAMA_EMBED_MODEL='.Length)
    $identity = if ($Profile -eq 'baseline') { $model } else { "$model|retrieval-v1" }
    $identitySql = $identity.Replace("'", "''")
    $deadline = [DateTime]::UtcNow.AddMinutes(3)
    do {
        $healthy = $false
        try { $healthy = (Invoke-RestMethod 'http://127.0.0.1:8090/health' -TimeoutSec 3).status -eq 'healthy' } catch { }
        $pending = docker exec personal-os-v2-db-1 psql -X -U personal_os_v2 -d personal_os_v2 -Atc "SELECT count(*) FROM search_chunks c JOIN search_sources s ON c.kind=s.kind AND c.source_id=s.id WHERE NOT s.is_deleted AND (c.source_version<>s.version OR c.embedding IS NULL OR c.embedding_model IS DISTINCT FROM '$identitySql');"
        if ($healthy -and $LASTEXITCODE -eq 0 -and $pending -eq '0') {
            Write-Output "Profile $Profile is healthy; all active embeddings use $identity."
            return
        }
        Start-Sleep -Seconds 2
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Profile $Profile did not become ready within 3 minutes"
} finally {
    foreach ($key in $previous.Keys) { [Environment]::SetEnvironmentVariable($key, $previous[$key], 'Process') }
    Pop-Location
}
