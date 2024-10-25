using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("AASWebUIUploadService")]
public class AASWebUIUploadController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;

    public AASWebUIUploadController(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    [HttpPost("uploadAASX")]
    public async Task<IActionResult> UploadAASX(IFormFile aasxFile, [FromForm] string specificAssetId, [FromForm] bool discover = true)
    {
        Console.WriteLine("Received form data:");
        foreach (var key in Request.Form.Keys)
        {
            Console.WriteLine($"{key}: {Request.Form[key]}");
        }
        Console.WriteLine($"About to call ProcessAASXFile with specificAssetId: {specificAssetId}");
        return await ProcessAASXFile(aasxFile, specificAssetId, discover);
    }

    [HttpPost("uploadAASXDirectory")]
    public async Task<IActionResult> UploadAASXDirectory(string directoryPath, bool discover = true)
    {
        // Adjust directory path to the container's mounted path
        string containerDirectoryPath = Path.Combine("/app/Verwaltungsschalen", directoryPath);

        // Log the received directoryPath
        Console.WriteLine($"Received directoryPath: {directoryPath}");
        Console.WriteLine($"Container directoryPath: {containerDirectoryPath}");

        // Check if the directory exists
        if (string.IsNullOrEmpty(containerDirectoryPath) || !Directory.Exists(containerDirectoryPath))
        {
            Console.WriteLine($"Invalid directory path: {containerDirectoryPath}");
            return BadRequest("Valid directory path is required.");
        }

        var aasxFiles = Directory.GetFiles(containerDirectoryPath, "*.aasx");
        if (aasxFiles.Length == 0)
        {
            Console.WriteLine($"No AASX files found in the specified directory: {containerDirectoryPath}");
            return BadRequest("No AASX files found in the specified directory.");
        }

        var results = new List<string>();

        foreach (var filePath in aasxFiles)
        {
            var fileName = Path.GetFileName(filePath);
            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            {
                var formFile = new FormFile(stream, 0, stream.Length, fileName, fileName);
                var result = await ProcessAASXFile(formFile, fileName, discover);
                results.Add(result.ToString());
            }
        }

        return Ok(results);
    }

    private async Task<IActionResult> ProcessAASXFile(IFormFile aasxFile, string specificAssetId, bool discover)
    {
        Console.WriteLine($"ProcessAASXFile received - File: {aasxFile?.FileName ?? "null"}, SpecificAssetId: {specificAssetId ?? "null"}, Discover: {discover}");

        if (aasxFile == null || aasxFile.Length == 0)
        {
            return BadRequest("AASX file is required.");
        }

        if (string.IsNullOrWhiteSpace(specificAssetId))
        {
            return BadRequest("Specific Asset ID is required.");
        }

        // Log file details
        Console.WriteLine($"File Name: {aasxFile.FileName}");
        Console.WriteLine($"File Length: {aasxFile.Length}");

        // Step 1: Upload the AASX file to the AAS repo
        var repoClient = _httpClientFactory.CreateClient();
        try
        {
            using (var content = new MultipartFormDataContent())
            {
                var fileStreamContent = new StreamContent(aasxFile.OpenReadStream());
                fileStreamContent.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("form-data")
                {
                    Name = "file",
                    FileName = aasxFile.FileName
                };
                fileStreamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                content.Add(fileStreamContent);

                var response = await repoClient.PostAsync("http://aas-environment-v3:8081/upload", content);

                if (!response.IsSuccessStatusCode)
                {
                    var responseContent = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"Upload failed: {response.StatusCode}, {responseContent}");
                    return StatusCode((int)response.StatusCode, $"Failed to upload AASX file: {responseContent}");
                }
                else
                {
                    Console.WriteLine("Upload succeeded.");
                }
            }
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"An error occurred while uploading the AASX file: {ex.Message}");
            return StatusCode(500, $"An error occurred while uploading the AASX file: {ex.Message}");
        }

        if (discover)
        {
            // Step 2: Create discovery entry
            var discoveryClient = _httpClientFactory.CreateClient();

            // Construct discovery entry using specificAssetId
            var discoveryEntry = new List<SpecificAssetId>
            {
                new SpecificAssetId
                {
                    Name = specificAssetId,
                    Value = "Asset_"+specificAssetId+"_Value"
                }
            };

            var discoveryContent = new StringContent(JsonSerializer.Serialize(discoveryEntry), Encoding.UTF8, "application/json");
            var discoveryEntryJson = JsonSerializer.Serialize(discoveryEntry);
            Console.WriteLine($"Discovery Content JSON: {discoveryEntryJson}");

            try
            {
                // Note: You might need to adjust how you generate the aasId for the URL
                // For now, I'm using the specificAssetId, but you might need to change this
                var encodedAasId = Base64UrlEncode($"https://aas.murrelektronik.com/{specificAssetId}/aas/1/0");
                var request = new HttpRequestMessage(HttpMethod.Post, $"http://aas-discovery-service:8081/lookup/shells/{encodedAasId}")
                {
                    Content = discoveryContent
                };
                request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

                var discoveryResponse = await discoveryClient.SendAsync(request);
                var discoveryResponseContent = await discoveryResponse.Content.ReadAsStringAsync();
                var discoveryResponseHeaders = discoveryResponse.Headers.ToString();
                Console.WriteLine($"Discovery Response: {discoveryResponse.StatusCode}, {discoveryResponseContent}");
                Console.WriteLine($"Discovery Response Headers: {discoveryResponseHeaders}");

                if (!discoveryResponse.IsSuccessStatusCode)
                {
                    return StatusCode((int)discoveryResponse.StatusCode, $"Failed to link in Discovery: {discoveryResponseContent}");
                }
            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine($"An error occurred while linking in Discovery: {ex.Message}");
                return StatusCode(500, $"An error occurred while linking in Discovery: {ex.Message}");
            }
        }

        // Return appropriate message based on actions taken
        return discover ? Ok("AASX uploaded and linked in Discovery.") : Ok("AASX uploaded.");
    }

    private string Base64UrlEncode(string input)
    {
        var byteArray = Encoding.UTF8.GetBytes(input);
        var base64 = Convert.ToBase64String(byteArray);
        return base64.Replace("+", "-").Replace("/", "_").Replace("=", "");
    }

    public class ShellDescriptor
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }
    
        [JsonPropertyName("administration")]
        public Administration Administration { get; set; }
    
        [JsonPropertyName("assetInformation")]
        public AssetInformation AssetInformation { get; set; }
    
        [JsonPropertyName("idShort")]
        public string IdShort { get; set; }
    }

    public class Administration
    {
        [JsonPropertyName("revision")]
        public string Revision { get; set; }
    }

    public class AssetInformation
    {
        [JsonPropertyName("assetKind")]
        public string AssetKind { get; set; }
    
        [JsonPropertyName("globalAssetId")]
        public string GlobalAssetId { get; set; }
    
        [JsonPropertyName("specificAssetIds")]
        public List<SpecificAssetId> SpecificAssetIds { get; set; }
    }

    public class SpecificAssetId
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }
    
        [JsonPropertyName("value")]
        public string Value { get; set; }
    }

    public class ShellsResponse
    {
        [JsonPropertyName("paging_metadata")]
        public PagingMetadata PagingMetadata { get; set; }

        [JsonPropertyName("result")]
        public List<ShellDescriptor> Result { get; set; }
    }

    public class PagingMetadata
    {
        // Define properties for paging metadata if needed
    }

    public class RegistryEntry
    {
        [JsonPropertyName("idShort")]
        public string IdShort { get; set; }
    
        [JsonPropertyName("id")]
        public string Id { get; set; }
    
        [JsonPropertyName("assetKind")]
        public string AssetKind { get; set; }
    
        [JsonPropertyName("administration")]
        public Administration Administration { get; set; }
    
        [JsonPropertyName("endpoints")]
        public List<Endpoint> Endpoints { get; set; }
    
        [JsonPropertyName("description")]
        public List<Description> Description { get; set; }
    }


    public class Endpoint
    {
        [JsonPropertyName("protocolInformation")]
        public ProtocolInformation ProtocolInformation { get; set; }
    
        [JsonPropertyName("interface")]
        public string Interface { get; set; }
    }

    public class ProtocolInformation
    {
        [JsonPropertyName("href")]
        public string Href { get; set; }
    
        [JsonPropertyName("endpointProtocol")]
        public string EndpointProtocol { get; set; }
    
        [JsonPropertyName("subprotocol")]
        public string Subprotocol { get; set; }
    }

    public class Description
    {
        [JsonPropertyName("language")]
        public string Language { get; set; }
    
        [JsonPropertyName("text")]
        public string Text { get; set; }
    }
}
