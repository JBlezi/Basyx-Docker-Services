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
        var matchingAssetIds = new List<string>();

        foreach (var assetId in request.AssetIds)
        {
            var assetAasData = await GetAasDataAsync(assetId, includeSubmodels: true);

            if (assetAasData != null)
            {
                foreach (var submodel in assetAasData.Submodels)
                {
                    if (request.SubmodelIds.Contains(submodel.GetProperty("id").GetString()))
                    {
                        var submodelElements = submodel.GetProperty("submodelElements").EnumerateArray();
                        foreach (var element in submodelElements)
                        {
                            if (element.TryGetProperty("value", out var value) && value.TryGetProperty("keys", out var keys))
                            {
                                foreach (var key in keys.EnumerateArray())
                                {
                                    if (key.GetProperty("value").GetString() == articleAasId)
                                    {
                                        matchingAssetIds.Add(assetId);
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        return Ok(matchingAssetIds);
    }

    private async Task<string> GetAasIdAsync(string assetId)
    {
        var client = _httpClientFactory.CreateClient();
        var response = await client.GetAsync($"http://aas-lookup-service:80/AASLookup/lookup?assetId={assetId}");
        
        _logger.LogInformation("AAS Api Call Response Status Code: {StatusCode}", response.StatusCode);
        
        var content = await response.Content.ReadAsStringAsync();
        _logger.LogInformation("AAS Api Call Content ReadAsString: {Content}", content);

        try
        {
            var jsonArray = JsonDocument.Parse(content).RootElement.EnumerateArray();
            _logger.LogInformation("AAS enumerated Array: {Array}", jsonArray);

            foreach (var item in jsonArray)
            {
                if (item.TryGetProperty("assetAdministrationShells", out var shells))
                {
                    foreach (var shell in shells.EnumerateArray())
                    {
                        if (shell.TryGetProperty("id", out var idProperty))
                        {
                            return idProperty.GetString();
                        }
                    }
                }
            }

            throw new Exception("AAS id not found in the response.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing AAS response");
            throw;
        }
    }


    private async Task<JsonObjectWrapper> GetAasDataAsync(string assetId, bool includeSubmodels)
    {
        var client = _httpClientFactory.CreateClient();
        var response = await client.GetAsync($"http://aas-lookup-service:80/AASLookup/lookup?assetId={assetId}&submodels={includeSubmodels}");
        
        _logger.LogInformation("AAS Api Call Response Status Code: {StatusCode}", response.StatusCode);
        
        var content = await response.Content.ReadAsStringAsync();
        _logger.LogInformation("AAS Api Call Content ReadAsString: {Content}", content);

        try
        {
            var jsonArray = JsonDocument.Parse(content).RootElement.EnumerateArray();
            _logger.LogInformation("AAS enumerated Array: {Array}", jsonArray);

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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing AAS response");
            throw;
        }
    }
}

public class LookupRequest
{
    public string ArticleAssetId { get; set; }
    public List<string> AssetIds { get; set; }
    public List<string> SubmodelIds { get; set; }
}
