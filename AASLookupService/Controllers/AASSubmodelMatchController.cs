using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using System.Text;

[ApiController]
[Route("[controller]")]
public class AASSubmodelMatchController : ControllerBase
{
    private readonly HttpClient _httpClient;

    public AASSubmodelMatchController(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    [HttpPost("match")]
    public async Task<IActionResult> MatchSubmodels(string articleAssetId, List<string> testAdapterAssetIds, List<string> testDeviceAssetIds)
    {
        try
        {
            // Encode the incoming Asset IDs
            string encodedArticleAssetId = Base64UrlEncode(articleAssetId);
            var encodedTestAdapterAssetIds = testAdapterAssetIds.ConvertAll(Base64UrlEncode);
            var encodedTestDeviceAssetIds = testDeviceAssetIds.ConvertAll(Base64UrlEncode);

            // Step 1: Article AAS Lookup
            var articleAas = await GetAasFromAssetId(encodedArticleAssetId);

            // Step 2: Test Adapter Matching
            var matchingAdapters = new List<string>();
            foreach (var encodedAdapterAssetId in encodedTestAdapterAssetIds)
            {
                var adapterAas = await GetAasFromAssetId(encodedAdapterAssetId);

                if (IsAdapterCompatibleWithArticle(articleAas, adapterAas))
                {
                    matchingAdapters.Add(adapterAas["assetAdministrationShells"][0]["id"].ToString());
                }
            }

            // Step 3: Test Device Matching
            var finalMatches = new List<(string AdapterAasId, string DeviceAasId)>();
            foreach (var adapterAasId in matchingAdapters)
            {
                var adapterAas = await GetAasFromAssetId(Base64UrlEncode(adapterAasId));
                foreach (var encodedDeviceAssetId in encodedTestDeviceAssetIds)
                {
                    var deviceAas = await GetAasFromAssetId(encodedDeviceAssetId);

                    if (IsDeviceCompatibleWithAdapter(adapterAas, deviceAas))
                    {
                        finalMatches.Add((adapterAasId, deviceAas["assetAdministrationShells"][0]["id"].ToString()));
                    }
                }
            }

            return Ok(finalMatches);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"An error occurred: {ex.Message}");
        }
    }

    private async Task<JObject> GetAasFromAssetId(string encodedAssetId)
    {
        var response = await _httpClient.GetAsync($"http://aas-lookup-service:80/AASLookup/lookup?assetId={encodedAssetId}&submodels=true");
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        return JObject.Parse(content);
    }

    private bool IsAdapterCompatibleWithArticle(JObject article, JObject adapter)
    {
        var articleProperties = GetTechnicalProperties(article);
        var adapterProperties = GetTechnicalProperties(adapter);

        return articleProperties.Count == adapterProperties.Count &&
               articleProperties.All(kvp => adapterProperties.TryGetValue(kvp.Key, out var value) && value.Equals(kvp.Value));
    }

    private Dictionary<string, string> GetTechnicalProperties(JObject aas)
    {
        var result = new Dictionary<string, string>();

        var submodels = aas["submodels"] as JArray;
        var technicalDataSubmodel = submodels?.FirstOrDefault(sm => sm["idShort"]?.ToString() == "TechnicalData");

        if (technicalDataSubmodel != null)
        {
            var submodelElements = technicalDataSubmodel["submodelElements"] as JArray;
            var technicalProperties = submodelElements?.FirstOrDefault(sme => sme["idShort"]?.ToString() == "TechnicalProperties");

            if (technicalProperties != null)
            {
                var properties = technicalProperties["value"] as JArray;
                foreach (var prop in properties ?? Enumerable.Empty<JToken>())
                {
                    var idShort = prop["idShort"]?.ToString();
                    var value = prop["value"]?.ToString();

                    if (idShort == "HousingNumber" || idShort == "SupportedProtocol")
                    {
                        result[idShort] = value;
                    }
                }
            }
        }

        return result;
    }

    private bool IsDeviceCompatibleWithAdapter(JObject adapter, JObject device)
    {
        var adapterProperties = GetDeviceCompatibilityProperties(adapter);
        var deviceProperties = GetDeviceCompatibilityProperties(device);

        return adapterProperties.Count == deviceProperties.Count &&
               adapterProperties.All(kvp => deviceProperties.TryGetValue(kvp.Key, out var value) && value.Equals(kvp.Value));
    }

    private Dictionary<string, string> GetDeviceCompatibilityProperties(JObject aas)
    {
        var result = new Dictionary<string, string>();

        var submodels = aas["submodels"] as JArray;

        // Get ElectricalConnectors version
        var interfaceConnectorsSubmodel = submodels?.FirstOrDefault(sm => sm["idShort"]?.ToString() == "InterfaceConnectors");
        if (interfaceConnectorsSubmodel != null)
        {
            var submodelElements = interfaceConnectorsSubmodel["submodelElements"] as JArray;
            var electricalConnectors = submodelElements?.FirstOrDefault(sme => sme["idShort"]?.ToString() == "ElectricalConnectors");

            if (electricalConnectors != null)
            {
                var electricalInterfaceVersion = electricalConnectors["value"]?
                    .FirstOrDefault(v => v["idShort"]?.ToString() == "ElectricalInterfaceVersion");

                if (electricalInterfaceVersion != null)
                {
                    result["ElectricalConnectors"] = electricalInterfaceVersion["value"]?.ToString();
                }
            }
        }

        // Get HardwareVersion
        var nameplateSubmodel = submodels?.FirstOrDefault(sm => sm["idShort"]?.ToString() == "Nameplate");
        if (nameplateSubmodel != null)
        {
            var submodelElements = nameplateSubmodel["submodelElements"] as JArray;
            var hardwareVersion = submodelElements?.FirstOrDefault(sme => sme["idShort"]?.ToString() == "HardwareVersion");

            if (hardwareVersion != null)
            {
                var versionValue = hardwareVersion["value"] as JArray;
                var englishVersion = versionValue?.FirstOrDefault(v => v["language"]?.ToString() == "en");

                if (englishVersion != null)
                {
                    result["HardwareVersion"] = englishVersion["text"]?.ToString();
                }
            }
        }

        return result;
    }

    private string Base64UrlEncode(string input)
    {
        var inputBytes = Encoding.UTF8.GetBytes(input);
        return Convert.ToBase64String(inputBytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}