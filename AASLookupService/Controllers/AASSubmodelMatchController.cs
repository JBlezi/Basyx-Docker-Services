using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

[ApiController]
[Route("[controller]")]
public class AASSubmodelMatchController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AASSubmodelMatchController> _logger;

    public AASSubmodelMatchController(IHttpClientFactory httpClientFactory, ILogger<AASSubmodelMatchController> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    [HttpPost("lookup-assets")]
    public async Task<IActionResult> LookupAssets([FromBody] LookupRequest request)
    {
        if (string.IsNullOrEmpty(request.ArticleAssetId) || request.AssetIds == null || request.SubmodelIds == null)
        {
            return BadRequest("Invalid request parameters.");
        }

        var articleAasId = await GetAasIdAsync(request.ArticleAssetId);
        _logger.LogInformation("Fetched Article AAS ID: {ArticleAasId}", articleAasId);
        var matchingAssetIds = new List<string>();

        foreach (var assetId in request.AssetIds)
        {
            var assetAasData = await GetAasDataAsync(assetId, includeSubmodels: true);
            _logger.LogInformation("Fetched AAS Data for Asset ID: {AssetId}, Data: {AssetAasData}", assetId, assetAasData);

            if (assetAasData != null)
            {
                foreach (var submodel in assetAasData.Submodels)
                {
                    if (submodel.ValueKind == JsonValueKind.Object && submodel.TryGetProperty("id", out var submodelId))
                    {
                        _logger.LogInformation("Checking Submodel ID: {SubmodelId} for Asset ID: {AssetId}", submodelId.GetString(), assetId);
                        if (request.SubmodelIds.Contains(submodelId.GetString()))
                        {
                            {
                                _logger.LogInformation("Match found for Asset ID: {AssetId} with Article AAS ID: {ArticleAasId}", assetId, articleAasId);
                                matchingAssetIds.Add(assetId);
                                break;
                            }
                        }
                    }
                }
            }
        }

        // Log the matchingAssetIds
        _logger.LogInformation("Response of AASSubmodelMatchController: {MatchingAssetIds}", JsonSerializer.Serialize(matchingAssetIds));
        
        return Ok(new { Message = "Matching Asset IDs", matchingAssetIds });
    }

    private async Task<string> GetAasIdAsync(string assetId)
    {
        var client = _httpClientFactory.CreateClient();
        var response = await client.GetAsync($"http://aas-lookup-service:80/AASLookup/lookup?assetId={assetId}");

        var content = await response.Content.ReadAsStringAsync();
        Console.WriteLine("Response Content: " + content); // Debug log to print the entire response

        try
        {
            var jsonArray = JsonDocument.Parse(content).RootElement.EnumerateArray();

            // Assuming the first element in the array contains the AAS data
            var aasDataWrapper = jsonArray.FirstOrDefault();
            if (aasDataWrapper.ValueKind == JsonValueKind.Object && aasDataWrapper.TryGetProperty("assetAdministrationShells", out var assetAdminShells))
            {
                var aasData = assetAdminShells.EnumerateArray().FirstOrDefault();
                if (aasData.ValueKind == JsonValueKind.Object && aasData.TryGetProperty("id", out var idProperty))
                {
                    return idProperty.GetString();
                }
                else
                {
                    throw new Exception("AAS id not found in the assetAdministrationShells.");
                }
            }
            else
            {
                throw new Exception("AAS assetAdministrationShells not found in the response.");
            }
        }
        catch (Exception ex)
        {
            throw new Exception("Error parsing AAS response", ex);
        }
    }

    private async Task<JsonObjectWrapper> GetAasDataAsync(string assetId, bool includeSubmodels)
    {
        var client = _httpClientFactory.CreateClient();
        var response = await client.GetAsync($"http://aas-lookup-service:80/AASLookup/lookup?assetId={assetId}&submodels={includeSubmodels}");
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync();
        var jsonArray = JsonDocument.Parse(content).RootElement.EnumerateArray();
        
        // Assuming the first element in the array contains the AAS data
        var aasData = jsonArray.FirstOrDefault();
        
        if (aasData.ValueKind == JsonValueKind.Object)
        {
            return JsonSerializer.Deserialize<JsonObjectWrapper>(aasData.GetRawText());
        }
        else
        {
            throw new Exception("AAS data not found in the response.");
        }
    }

    private string GetSnippet(string content, int length = 300)
    {
        if (string.IsNullOrEmpty(content))
        {
            return string.Empty;
        }

        return content.Length <= length ? content : content.Substring(0, length) + "...";
    }
}

public class LookupRequest
{
    public string ArticleAssetId { get; set; }
    public List<string> AssetIds { get; set; }
    public List<string> SubmodelIds { get; set; }
}
