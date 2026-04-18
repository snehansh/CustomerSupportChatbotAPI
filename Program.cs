using Azure;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.AI;

using Microsoft.Azure.Cosmos;
using dotenv.net;
using Azure.Core;

var MyAllowSpecificOrigins = "_myPolicy";

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCors(options =>
{
    options.AddPolicy(name: MyAllowSpecificOrigins,
        policy =>
        {
            // policy.WithOrigins("http://localhost:5173")
            policy.WithOrigins("https://customer-support-chatbot.pages.dev")
                .AllowAnyHeader()
                .AllowAnyMethod();
        }
    );
});

var app = builder.Build();
app.UseCors();

IConfigurationRoot config = new ConfigurationBuilder().AddUserSecrets<Program>().Build();

DotEnv.Load(); // loads .env into OS env vars (no-op if file doesn't exist)

string endpoint = Environment.GetEnvironmentVariable("OPENAI_ENDPOINT")
    ?? throw new InvalidOperationException("OPENAI_ENDPOINT environment variable is not set.");
string deployment = Environment.GetEnvironmentVariable("OPENAI_MODEL")
    ?? throw new InvalidOperationException("OPENAI_MODEL environment variable is not set.");
string azureClientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID") ?? "";
string azureClientSecret = Environment.GetEnvironmentVariable("AZURE_CLIENT_SECRET") ?? "";
string azureTenantId = Environment.GetEnvironmentVariable("AZURE_TENANT_ID") ?? "";

IChatClient chatClient =
    new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential())
    .GetChatClient(deployment)
    .AsIChatClient();


// Start the conversation with context for the AI model

ChatMessage sysMsg = new(ChatRole.System, """
            You are a helpful assistant representing "The Office Store". Your job is to help customers in ordering office supplies.
            Your job is to interface with customers, understand their requirements and present them with products or solutions from the store.
            You introduce yourself as an AI assistant. You greet the customer and ask them what you can help them with.
            - If customer asks for item that store carries then lookup your instruction and ask them to check in the department that has been mentioned for the item in your instruction.
            - If customer asks for anything other than the items store carries than determine if the item is an office supply. If it is then apologize to the customer for not carrying the item and let them know that we are a new store and always adding new items. Ask them to check back later.
            - If customer asks for anything other than the items store carries and you determine that the item is not an office supply then apologize to the customer for not carrying the item in the store and inform that we only sell office products and services.
            Store carries following items:
                Laptop - Department: Electronics
                Monitor - Department: Electronics
                Office Desk - Department: Furniture
                Office Chair - Department: Furniture
                Stationary items - Pen, Pencil, diary, other common stationary items
                Printer - Department: Electronics
        """);

app.MapPost("/generate", async (PromptRequest req) =>
{
    List<ChatMessage> history = [];
    ChatRepository repo = new();
    ChatHistoryResult result = await repo.GetChatHistory();

    if (string.IsNullOrWhiteSpace(req.UserPrompt))
        return Results.Ok(new GeneratedResponse("", result.ChatMessages));

    if (result.ChatMessages != null)
        history.AddRange(result.ChatMessages.ConvertAll(x => new ChatMessage(x.Role.Value, x.Content)));
    else
        history.Add(sysMsg);

    history.Add(new ChatMessage(ChatRole.User, req.UserPrompt));

    string response = "";
    await foreach (ChatResponseUpdate item in
            chatClient.GetStreamingResponseAsync(history))
    {
        response += item.Text;
    }
    history.Add(new ChatMessage(ChatRole.Assistant, response));

    List<ChatMessageDto> records = history.ConvertAll(x => new ChatMessageDto(x));
    await repo.SaveChatHistory(records);

    return Results.Ok(new GeneratedResponse(response, records));
}).RequireCors(MyAllowSpecificOrigins);

app.Run();

public record PromptRequest(string UserPrompt);
public record GeneratedResponse(string ResponseText, IEnumerable<ChatMessageDto> ChatHistory);
public class ChatMessageDto
{
    public ChatRole? Role { get; set; }
    public string? Content { get; set; }

    public ChatMessageDto() { }

    public ChatMessageDto(ChatMessage chatMessage)
    {
        Role = chatMessage.Role;
        Content = chatMessage?.Text;
    }
}

public record ChatEntity(string id, string category, List<ChatMessageDto> ChatMessages);

public record ChatHistoryResult(List<ChatMessageDto>? ChatMessages = null, string? ErrorMessage = null, bool IsSuccess = false);

public class ChatRepository()
{
    public async Task<string> SaveChatHistory(List<ChatMessageDto> chatMessages)
    {
        try
        {
            string databaseName = "myDatabase"; // Name of the database to create or use
            string containerName = "myContainer"; // Name of the container to create or use

            // Load environment variables from .env file (no-op if file doesn't exist)
            DotEnv.Load();
            string cosmosDbAccountUrl = Environment.GetEnvironmentVariable("DOCUMENT_ENDPOINT") ?? "";
            string azureClientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID") ?? "";
            string azureClientSecret = Environment.GetEnvironmentVariable("AZURE_CLIENT_SECRET") ?? "";
            string azureTenantId = Environment.GetEnvironmentVariable("AZURE_TENANT_ID") ?? "";

            if (
                string.IsNullOrEmpty(cosmosDbAccountUrl) ||
                string.IsNullOrEmpty(azureClientId) ||
                string.IsNullOrEmpty(azureClientSecret) ||
                string.IsNullOrEmpty(azureTenantId)
                )
                return "Required environmemt variables are missing.";

            // CREATE THE COSMOS DB CLIENT USING THE ACCOUNT URL AND KEY
            CosmosClient cosmosClient = new(accountEndpoint: cosmosDbAccountUrl, tokenCredential: new DefaultAzureCredential());

            // CREATE A DATABASE IF IT DOESN'T ALREADY EXIST
            Database database = await cosmosClient.CreateDatabaseIfNotExistsAsync(databaseName);
            Console.WriteLine($"Created or retrieved database: {database.Id}");

            // CREATE A CONTAINER WITH A SPECIFIED PARTITION KEY
            Container container = await database.CreateContainerIfNotExistsAsync(
                id: containerName,
                partitionKeyPath: "/category"
            );
            Console.WriteLine($"Created or retrieved container: {container.Id}");

            ChatEntity newItem = new(id: "1", category: "sales-rep", chatMessages);

            // ADD THE ITEM TO THE CONTAINER
            var createResponse = await container.UpsertItemAsync(
                item: newItem,
                partitionKey: new PartitionKey(newItem.category)
            );

            Console.WriteLine($"Created item with ID: {createResponse.Resource.id}");
            Console.WriteLine($"Request charge: {createResponse.RequestCharge} RUs");
            return createResponse.Resource.id;
        }
        catch (CosmosException ex)
        {
            // Handle Cosmos DB-specific exceptions
            // Log the status code and error message for debugging
            Console.WriteLine($"Cosmos DB Error: {ex.StatusCode} - {ex.Message}");
            return ex.Message;
        }
        catch (Exception ex)
        {
            // Handle general exceptions
            // Log the error message for debugging
            Console.WriteLine($"Error: {ex.Message}");
            return ex.Message;
        }
    }

    public async Task<ChatHistoryResult> GetChatHistory()
    {
        try
        {
            string databaseName = "myDatabase"; // Name of the database to create or use
            string containerName = "myContainer"; // Name of the container to create or use

            // Load environment variables from .env file (no-op if file doesn't exist)
            DotEnv.Load();
            string cosmosDbAccountUrl = Environment.GetEnvironmentVariable("DOCUMENT_ENDPOINT") ?? "";
            string azureClientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID") ?? "";
            string azureClientSecret = Environment.GetEnvironmentVariable("AZURE_CLIENT_SECRET") ?? "";
            string azureTenantId = Environment.GetEnvironmentVariable("AZURE_TENANT_ID") ?? "";

            if (
                string.IsNullOrEmpty(cosmosDbAccountUrl) ||
                string.IsNullOrEmpty(azureClientId) ||
                string.IsNullOrEmpty(azureClientSecret) ||
                string.IsNullOrEmpty(azureTenantId)
                )
                return new ChatHistoryResult(ChatMessages: null, ErrorMessage: "Required environmemt variables are missing.", IsSuccess: false);


            // CREATE THE COSMOS DB CLIENT USING THE ACCOUNT URL AND KEY
            CosmosClient cosmosClient = new(accountEndpoint: cosmosDbAccountUrl, tokenCredential: new DefaultAzureCredential());


            // CREATE A DATABASE IF IT DOESN'T ALREADY EXIST
            Database database = await cosmosClient.CreateDatabaseIfNotExistsAsync(databaseName);

            // CREATE A CONTAINER WITH A SPECIFIED PARTITION KEY
            Container container = await database.CreateContainerIfNotExistsAsync(
                id: containerName,
                partitionKeyPath: "/category"
            );

            ChatEntity item = await container.ReadItemAsync<ChatEntity>(id: "1", partitionKey: new PartitionKey("sales-rep"));
            return new ChatHistoryResult(ChatMessages: item.ChatMessages, ErrorMessage: null, IsSuccess: true);
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return new ChatHistoryResult(ChatMessages: null, ErrorMessage: "No items found", IsSuccess: false);
        }
        catch (Exception ex)
        {
            throw ex;
        }
    }
}