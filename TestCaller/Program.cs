using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.UserSecrets;

const string JwtBearerTokenSecretKey = "Caller:JwtBearerToken";
const string SetJwtBearerTokenOptionName = "--set-jwt-bearer-token";

var jwtBearerTokenToStore = GetOptionValue(args, SetJwtBearerTokenOptionName);

if (jwtBearerTokenToStore is not null)
{
    SetUserSecret(GetUserSecretsId(), JwtBearerTokenSecretKey, jwtBearerTokenToStore);
    Console.WriteLine("Stored JWT bearer token in user secrets.");
    return;
}

var commandLineArgs = NormalizeCommandLineArgs(args);
var switchMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    ["--base-url"] = "Caller:BaseUrl",
    ["--path"] = "Caller:Path",
    ["--fake-x-forwarded-for"] = "Caller:FakeXForwardedFor",
    ["--parallel-threads"] = "Caller:ParallelThreads",
    ["--request-count"] = "Caller:RequestCount",
    ["--timeout-seconds"] = "Caller:TimeoutSeconds"
};

var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .AddUserSecrets<Program>(optional: true)
    .AddEnvironmentVariables()
    .AddCommandLine(commandLineArgs, switchMappings)
    .Build();

var options = configuration.GetSection("Caller").Get<CallerOptions>()
    ?? throw new InvalidOperationException("Missing Caller configuration.");

if (options.ParallelThreads <= 0)
{
    throw new InvalidOperationException("Caller:ParallelThreads must be greater than 0.");
}

if (options.RequestCount <= 0)
{
    throw new InvalidOperationException("Caller:RequestCount must be greater than 0.");
}

if (string.IsNullOrWhiteSpace(options.BaseUrl))
{
    throw new InvalidOperationException("Caller:BaseUrl is required.");
}

if (string.IsNullOrWhiteSpace(options.Path))
{
    throw new InvalidOperationException("Caller:Path is required.");
}

using var httpClient = new HttpClient
{
    BaseAddress = new Uri(options.BaseUrl),
    Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds)
};

var hasJwtBearerToken = !string.IsNullOrWhiteSpace(options.JwtBearerToken);

Console.WriteLine(
    $"Sending {options.RequestCount} requests to {httpClient.BaseAddress}{options.Path} with parallelThreads={options.ParallelThreads}, fakeXForwardedFor={options.FakeXForwardedFor}, bearerTokenConfigured={hasJwtBearerToken}");

await Parallel.ForEachAsync(
    Enumerable.Range(1, options.RequestCount),
    new ParallelOptions { MaxDegreeOfParallelism = options.ParallelThreads },
    async (requestNumber, cancellationToken) =>
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, options.Path);
            var forwardedFor = options.FakeXForwardedFor ? GenerateRandomIpv4() : null;

            if (forwardedFor is not null)
            {
                request.Headers.Add("X-Forwarded-For", forwardedFor);
            }

            if (hasJwtBearerToken)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.JwtBearerToken);
            }

            using var response = await httpClient.SendAsync(request, cancellationToken);
            await response.Content.ReadAsByteArrayAsync(cancellationToken);

            stopwatch.Stop();
            Console.WriteLine(
                $"request={requestNumber} statusCode={(int)response.StatusCode} reasonPhrase={response.ReasonPhrase} responseTimeMs={stopwatch.Elapsed.TotalMilliseconds:F0}{FormatForwardedFor(forwardedFor)}");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Console.WriteLine(
                $"request={requestNumber} statusCode=ERROR responseTimeMs={stopwatch.Elapsed.TotalMilliseconds:F0} error={ex.Message}");
        }
    });

static string GenerateRandomIpv4()
{
    Span<int> octets = stackalloc int[4];

    octets[0] = Random.Shared.Next(1, 224);
    octets[1] = Random.Shared.Next(0, 256);
    octets[2] = Random.Shared.Next(0, 256);
    octets[3] = Random.Shared.Next(1, 255);

    return $"{octets[0]}.{octets[1]}.{octets[2]}.{octets[3]}";
}

static string FormatForwardedFor(string? forwardedFor) =>
    forwardedFor is null ? string.Empty : $" xForwardedFor={forwardedFor}";

static string? GetOptionValue(string[] args, string optionName)
{
    for (var i = 0; i < args.Length; i++)
    {
        var arg = args[i];

        if (arg.StartsWith($"{optionName}=", StringComparison.OrdinalIgnoreCase))
        {
            return arg[(optionName.Length + 1)..];
        }

        if (!string.Equals(arg, optionName, StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (i == args.Length - 1 || LooksLikeSwitch(args[i + 1]))
        {
            throw new InvalidOperationException($"{optionName} requires a value.");
        }

        return args[i + 1];
    }

    return null;
}

static string[] NormalizeCommandLineArgs(string[] args)
{
    var normalizedArgs = new List<string>(args.Length);

    for (var i = 0; i < args.Length; i++)
    {
        var arg = args[i];

        if (string.Equals(arg, "--fake-x-forwarded-for", StringComparison.OrdinalIgnoreCase)
            && (i == args.Length - 1 || LooksLikeSwitch(args[i + 1])))
        {
            normalizedArgs.Add($"{arg}=true");
            continue;
        }

        normalizedArgs.Add(arg);
    }

    return normalizedArgs.ToArray();
}

static bool LooksLikeSwitch(string value) =>
    value.StartsWith('-') || value.StartsWith('/');

static string GetUserSecretsId() =>
    Assembly.GetExecutingAssembly().GetCustomAttribute<UserSecretsIdAttribute>()?.UserSecretsId
    ?? throw new InvalidOperationException("TestCaller user secrets are not configured.");

static void SetUserSecret(string userSecretsId, string key, string value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        throw new InvalidOperationException($"{key} cannot be empty.");
    }

    var secretsFilePath = GetSecretsFilePath(userSecretsId);
    var secretsDirectoryPath = Path.GetDirectoryName(secretsFilePath)
        ?? throw new InvalidOperationException("Unable to resolve the user secrets directory.");

    Directory.CreateDirectory(secretsDirectoryPath);

    JsonObject root;
    if (File.Exists(secretsFilePath))
    {
        var content = File.ReadAllText(secretsFilePath);
        root = string.IsNullOrWhiteSpace(content)
            ? new JsonObject()
            : JsonNode.Parse(content) as JsonObject
                ?? throw new InvalidOperationException("User secrets file must contain a JSON object.");
    }
    else
    {
        root = new JsonObject();
    }

    SetJsonValue(root, key.Split(':', StringSplitOptions.RemoveEmptyEntries), value);

    File.WriteAllText(
        secretsFilePath,
        root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
}

static string GetSecretsFilePath(string userSecretsId)
{
    var secretsRoot = OperatingSystem.IsWindows()
        ? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft",
            "UserSecrets")
        : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".microsoft",
            "usersecrets");

    if (string.IsNullOrWhiteSpace(secretsRoot))
    {
        throw new InvalidOperationException("Unable to resolve the user secrets root directory.");
    }

    return Path.Combine(secretsRoot, userSecretsId, "secrets.json");
}

static void SetJsonValue(JsonObject root, IReadOnlyList<string> pathSegments, string value)
{
    var current = root;

    for (var i = 0; i < pathSegments.Count - 1; i++)
    {
        var segment = pathSegments[i];

        if (current[segment] is not JsonObject child)
        {
            child = new JsonObject();
            current[segment] = child;
        }

        current = child;
    }

    current[pathSegments[^1]] = value;
}

internal sealed class CallerOptions
{
    public string BaseUrl { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public string? JwtBearerToken { get; init; }
    public bool FakeXForwardedFor { get; init; }
    public int ParallelThreads { get; init; } = 5;
    public int RequestCount { get; init; } = 20;
    public int TimeoutSeconds { get; init; } = 30;
}
