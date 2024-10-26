using System;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Linq;
using System.Text.Json.Serialization;

[ApiController]
[Route("AASUploadService")]
public class AASUploadController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;

    public AASUploadController(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    [HttpPost("uploadAASX")]
    public async Task<IActionResult> UploadAASX(IFormFile aasxFile, bool register = true, bool discover = true)
    {
        return await ProcessAASXFile(aasxFile, register, discover);
    }

    [HttpPost("uploadAASXDirectory")]
    public async Task<IActionResult> UploadAASXDirectory(string directoryPath, bool register = true, bool discover = true)
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
                var result = await ProcessAASXFile(formFile, register, discover);
                results.Add(result.ToString());
            }
        }

        return Ok(results);
    }

    private async Task<IActionResult> ProcessAASXFile(IFormFile aasxFile, bool register, bool discover)
    {
        if (aasxFile == null || aasxFile.Length == 0)
        {
            return BadRequest("AASX file is required.");
        }

        if (discover && !register)
        {
            return BadRequest("Discovery can only be performed if registration is also enabled.");
        }
        
        // Log file details
        Console.WriteLine($"File Name: {aasxFile.FileName}");
        Console.WriteLine($"File Length: {aasxFile.Length}");

        try
        {
            using (var stream = aasxFile.OpenReadStream())
            {
                byte[] buffer = new byte[1024];
                int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                string previewContent = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                Console.WriteLine($"File Content Preview: {previewContent}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error reading file: {ex.Message}");
            return StatusCode(500, $"Error reading file: {ex.Message}");
        }

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
                    var responseHeaders = response.Headers.ToString();
                    Console.WriteLine($"Upload failed: {response.StatusCode}, {responseContent}");
                    Console.WriteLine($"Response Headers: {responseHeaders}");
                    if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
                    {
                        return Conflict("AASX file already exists in the repository.");
                    }
                    else
                    {
                        return StatusCode((int)response.StatusCode, $"Failed to upload AASX file: {responseContent}");
                    }
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
        
        if (register)
        {
            // Step 2: Retrieve the necessary information from the AAS for making the AAS registry entry
            var getShellsResponse = await repoClient.GetAsync("http://aas-environment-v3:8081/shells");
            getShellsResponse.EnsureSuccessStatusCode();

            var shellsContent = await getShellsResponse.Content.ReadAsStringAsync();
            // Deserialize using the ShellsResponse wrapper class
            var shellsResponse = JsonSerializer.Deserialize<ShellsResponse>(shellsContent);
            var shellsResult = shellsResponse.Result;

            var newestShell = shellsResult.LastOrDefault();
            if (newestShell == null)
            {
                return NotFound("No shell descriptors found.");
            }

            // Step 3: Extract information from the shell descriptor
            var aasId = newestShell.Id;
            var administrationRevision = newestShell.Administration?.Revision ?? "0";
            var assetKind = newestShell.AssetInformation.AssetKind;
            var idShort = newestShell.IdShort;
            var specificAssetIds = newestShell.AssetInformation.SpecificAssetIds;

            // Construct registry entry
            var registryEntry = new RegistryEntry
            {
                IdShort = idShort,
                Id = aasId,
                AssetKind = assetKind,
                Administration = new Administration { Revision = administrationRevision },
                Endpoints = new List<Endpoint>
                {
                    new Endpoint
                    {
                        ProtocolInformation = new ProtocolInformation
                        {
                            Href = $"http://localhost:8082/shells/{Base64UrlEncode(aasId)}",
                            EndpointProtocol = "HTTP",
                            Subprotocol = "AAS"
                        },
                        Interface = "https://admin-shell.io/aas/API/3/0/AasServiceSpecification/SSP-003"
                    }
                },
                Description = new List<Description>
                {
                    new Description
                    {
                        Language = "en-US",
                        Text = "Standardized digital representation of Murrelektroniks. It holds digital models of various aspects (submodels) and describes technical functionality exposed by them."
                    }
                }
            };

            // Log the registry entry before sending
            var registryEntryJson = JsonSerializer.Serialize(registryEntry);
            Console.WriteLine($"Registry Entry JSON: {registryEntryJson}");

            // Step 4: Upload the registry entry
            var registryClient = _httpClientFactory.CreateClient();
            var registryContent = new StringContent(JsonSerializer.Serialize(registryEntry), Encoding.UTF8, "application/json");
            try
            {
                var registryResponse = await registryClient.PostAsync($"http://aas-registry-v3:8080/api/v3.0/shell-descriptors", registryContent);
                var registryResponseContent = await registryResponse.Content.ReadAsStringAsync();
                var registryResponseHeaders = registryResponse.Headers.ToString();
                Console.WriteLine($"Registry Response: {registryResponse.StatusCode}, {registryResponseContent}");
                Console.WriteLine($"Registry Response Headers: {registryResponseHeaders}");

                if (!registryResponse.IsSuccessStatusCode)
                {
                    return StatusCode((int)registryResponse.StatusCode, $"Failed to register AAS: {registryResponseContent}");
                }
            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine($"An error occurred while posting to the registry: {ex.Message}");
                return StatusCode(500, $"An error occurred while posting to the registry: {ex.Message}");
            }

            // Step 5: If discover is true, construct and upload the discovery entry
            if (discover)
            {
                var discoveryClient = _httpClientFactory.CreateClient();

                // Modify discoveryEntry to match the expected JSON structure
                var discoveryEntry = specificAssetIds;

                var discoveryContent = new StringContent(JsonSerializer.Serialize(discoveryEntry), Encoding.UTF8, "application/json");
                var discoveryEntryJson = JsonSerializer.Serialize(discoveryEntry);
                Console.WriteLine($"Discovery Content JSON: {discoveryEntryJson}"); // Log the discovery content JSON

                // Create the request with the necessary headers
                var request = new HttpRequestMessage(HttpMethod.Post, $"http://aas-discovery-service:8081/lookup/shells/{Base64UrlEncode(aasId)}")
                {
                    Content = discoveryContent
                };
                request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
                discoveryContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

                var discoveryResponse = await discoveryClient.SendAsync(request);

                var discoveryResponseContent = await discoveryResponse.Content.ReadAsStringAsync();
                var discoveryResponseHeaders = discoveryResponse.Headers.ToString();
                Console.WriteLine($"Discovery Response: {discoveryResponse.StatusCode}, {discoveryResponseContent}"); // Log the discovery response
                Console.WriteLine($"Discovery Response Headers: {discoveryResponseHeaders}");

                if (!discoveryResponse.IsSuccessStatusCode)
                {
                    return StatusCode((int)discoveryResponse.StatusCode, $"Failed to link in Discovery: {discoveryResponseContent}");
                }

                return Ok("AASX uploaded, registered and linked in Discovery.");
            }

            // Return the registry entry for now
            return Ok("AASX uploaded and registered.");
        }
        else
        {
            return Ok("AASX uploaded.");
        }
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
