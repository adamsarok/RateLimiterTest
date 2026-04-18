using Bogus;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
var authenticationOptions = builder.Configuration.GetSection("Authentication").Get<AuthenticationOptions>() ?? new AuthenticationOptions();
var hasJwtBearerAuthentication = !string.IsNullOrWhiteSpace(authenticationOptions.Authority)
	&& !string.IsNullOrWhiteSpace(authenticationOptions.Audience);

//builder.Services.AddEndpointsApiExplorer();
//builder.Services.AddSwaggerGen();
builder.Services.AddOpenApi();
if (hasJwtBearerAuthentication) {
	builder.Services
		.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
		.AddJwtBearer(options => {
			options.Authority = authenticationOptions.Authority;
			options.Audience = authenticationOptions.Audience;
			options.MapInboundClaims = false;
			options.TokenValidationParameters = new TokenValidationParameters {
				NameClaimType = "name"
			};
			options.Events = new JwtBearerEvents {
				OnAuthenticationFailed = context => {
					var logger = context.HttpContext.RequestServices
						.GetRequiredService<ILoggerFactory>()
						.CreateLogger("JwtBearer");
					logger.LogWarning(context.Exception, "Bearer token authentication failed.");
					return Task.CompletedTask;
				}
			};
		});
}
builder.Services.AddRateLimiter(options => {
	options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
	options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext => {
		var partitionKey = GetRateLimitPartitionKey(httpContext);

		return RateLimitPartition.GetFixedWindowLimiter(
			partitionKey,
			_ => new FixedWindowRateLimiterOptions {
				PermitLimit = 20,
				Window = TimeSpan.FromMinutes(1),
				QueueLimit = 0,
				AutoReplenishment = true
			});
	});
});

var app = builder.Build();
var authenticationStartupMessage = hasJwtBearerAuthentication
	? $"JWT bearer authentication enabled. Authority={authenticationOptions.Authority} Audience={authenticationOptions.Audience}"
	: "JWT bearer authentication disabled. Configure Authentication:Authority and Authentication:Audience to enable it.";

Console.WriteLine(authenticationStartupMessage);
app.Logger.LogInformation(authenticationStartupMessage);

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment()) {
	//app.UseSwagger();
	//app.UseSwaggerUI();
	app.MapOpenApi();
}

app.UseHttpsRedirection();
if (hasJwtBearerAuthentication) {
	app.UseAuthentication();
}
app.Use(async (httpContext, next) => {
	var partitionKey = GetRateLimitPartitionKey(httpContext);
	var hasAuthorizationHeader = httpContext.Request.Headers.ContainsKey("Authorization");
	var isAuthenticated = httpContext.User.Identity?.IsAuthenticated ?? false;
	var oid = GetOid(httpContext.User) ?? "anonymous";
	var message =
		$"Request {httpContext.Request.Method} {httpContext.Request.Path} oid={oid} partitionKey={partitionKey}";

	Console.WriteLine(message);
	app.Logger.LogInformation(
		"Request {Method} {Path} authHeaderPresent={AuthHeaderPresent} isAuthenticated={IsAuthenticated} oid={Oid} partitionKey={PartitionKey}",
		httpContext.Request.Method,
		httpContext.Request.Path,
		hasAuthorizationHeader,
		isAuthenticated,
		oid,
		partitionKey);

	await next();
});
app.UseRateLimiter();


var faker = new Faker<Item>()
	.CustomInstantiator(i => new Item(
		i.IndexFaker,
		i.Commerce.ProductName(),
		i.Lorem.Sentence(),
		i.Random.Float(0.1f, 200)));
var items = faker.Generate(1000);

app.MapGet("/items", (int page, int pageSize) => {
	Thread.Sleep(1000); // Simulate some processing delay
	return new Response(pageSize, items.Count / pageSize, page, items.Skip((page - 1) * pageSize).Take(pageSize).ToList());
})
.WithName("GetItems");

app.Run();

static string GetRateLimitPartitionKey(HttpContext httpContext) {
	var oid = GetOid(httpContext.User);
	if (!string.IsNullOrEmpty(oid)) return $"oid:{oid}";
	var forwardedFor = httpContext.Request.Headers["X-Forwarded-For"].ToString(); //only trust x-forwarder for if coming from AGW, or ignore?
	var clientIp = GetClientIp(forwardedFor, httpContext.Connection.RemoteIpAddress?.ToString());
	return $"ip:{clientIp}";
}

static string? GetOid(ClaimsPrincipal user) =>
	user.FindFirst("oid")?.Value
	?? user.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value;

static string GetClientIp(string? forwardedFor, string? remoteIpAddress) {
	if (!string.IsNullOrWhiteSpace(forwardedFor)) {
		var firstForwardedIp = forwardedFor
			.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
			.FirstOrDefault();

		if (!string.IsNullOrWhiteSpace(firstForwardedIp)) {
			return firstForwardedIp;
		}
	}

	return remoteIpAddress ?? "unknown";
}

sealed class AuthenticationOptions {
	public string? Authority { get; init; }
	public string? Audience { get; init; }
}

record Response(int PageSize, int TotalPages, int Page, List<Item> Items);
record Item(int Id, string Name, string Description, float Weight);
