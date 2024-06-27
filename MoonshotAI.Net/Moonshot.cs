using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MoonshotAI.Net;

public static partial class Moonshot
{
    public enum ResponseFormat { Text, JsonObject }
    public enum MaxTokenPolicy { Default, Max }

    public struct Message(string role, string content)
    {
        public string role = role;
        public string content = content;
    }
    public struct Balance(float available, float voucher, float cash)
    {
        public float available_balance = available;
        public float voucher_balance = voucher;
        public float cash_balance = cash;
    }
    public struct Error(int code, string type, string message, string description)
    {
        public int code = code;
        public string type = type;
        public string message = message;
        public string description = description;

        public static int GetSimilarity(Error a, Error b)
        {
            if (a.code != b.code || a.type != b.type)
                return 0;
            var splittedA = a.message.Split(' ');
            var splittedB = b.message.Split(' ');
            var length = Math.Min(splittedA.Length, splittedB.Length);
            var similarity = 0;
            for (var i = 0; i < length; i++)
                if (splittedA[i] == splittedB[i])
                    similarity++;
            return similarity;
        }
    }
    public sealed class Exception(Error error) : System.Exception(
        $"HTTP Status Code: {error.code}\n" +
        $"Error Type      : {error.type}\n" +
        $"Error Message   : {error.message}\n" +
        $"Description(CN) : {error.description}")
    {
        public Error Error { get; private set; } = error;
    }
    public sealed class Session(string key, string model)
    {
        public event Action<Message>? OnMessageAdded;

        private readonly string key = key;
        private readonly string model = model;
        private readonly List<Message> messages = [];

        public async Task<string> ChatAsync(string message, CancellationToken cancellationToken = default)
        {
            var request = new Message("user", message);
            messages.Add(request);
            OnMessageAdded?.Invoke(request);
            var response = await Moonshot.ChatAsync(key, messages, model, cancellationToken: cancellationToken);
            messages.Add(response);
            OnMessageAdded?.Invoke(response);
            return response.content;
        }
    }

    public static IEnumerable<Error> ListErrors() => errors;
    public static async Task<string[]> ListModelIDsAsync(string key, CancellationToken cancellationToken = default)
    {
        var models = await ListModelsAsync(key, cancellationToken);
        return models?.Select(model => model.id!).ToArray() ?? [];
    }
    public static async Task<Balance> QueryBalanceAsync(string key, CancellationToken cancellationToken = default)
    {
        var response = await HTTPGetFromJsonAsync<BalanceResponse>("https://api.moonshot.cn/v1/users/me/balance", key, cancellationToken);
        return response!.data;
    }
    public static async Task<int> EstimateTokenCountAsync(string key, string modelID, IEnumerable<Message> messages, CancellationToken cancellationToken = default)
    {
        var url = "https://api.moonshot.cn/v1/tokenizers/estimate-token-count";
        var messagesArray = new JsonArray();
        foreach (var message in messages)
            messagesArray.Add(message);
        var root = new JsonObject([
            new("model", modelID),
            new("messages", messagesArray)
        ]);
        var requestJson = root.ToJsonString(serializerOptions);
        var response = await HTTPPostAsync(url, requestJson, key, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw await HTTPHandleErrorAsync(response, cancellationToken);
        var responseJsonString = await response.Content.ReadAsStringAsync(cancellationToken);
        var responseJson = JsonNode.Parse(responseJsonString);
        return responseJson!["data"]!["total_tokens"]!.GetValue<int>();
    }
    public static async Task<Message> ChatAsync(string key,
        ICollection<Message> messages, string model,
        int? maxTokens = null, MaxTokenPolicy maxTokenPolicy = MaxTokenPolicy.Default,
        float? temperature = null, float? topP = null, int? n = null,
        float? presencePenalty = null, float? frequencyPenalty = null,
        ResponseFormat? responseFormat = null, ICollection<string>? stop = null,
        bool? stream = null, CancellationToken cancellationToken = default)
    {
        if (messages.Count <= 0)
            throw new ArgumentException("messages are required");
        if (maxTokens == null && maxTokenPolicy == MaxTokenPolicy.Max)
        {
            var inputTokenCount = await EstimateTokenCountAsync(key, model, messages, cancellationToken);
            var rate = 1;
            var modelTokenFeature = model.Split('-')[^1];
            Dictionary<char, int> unitsMap = new()
            {
                { 'k', 1<<10 },
                { 'm', 1<<20 },
                { 'b', 1<<30 },
            };
            bool updated;
            do
            {
                updated = false;
                foreach (var unit in unitsMap)
                {
                    if (modelTokenFeature.EndsWith(unit.Key))
                    {
                        modelTokenFeature = modelTokenFeature.TrimEnd(unit.Key);
                        rate *= unit.Value;
                        updated = true;
                    }
                }
            } while (updated);
            maxTokens = int.Parse(modelTokenFeature) * rate - inputTokenCount;
        }
        var validateRangeFunc = (string name, dynamic? value,
            double min, double max, bool includeMin, bool includeMax) =>
        {
            if (value != null)
                if ((includeMin && value < min) ||
                   (!includeMin && value <= min) ||
                   (includeMax && value > max) ||
                   (!includeMax && value >= max))
                    throw new ArgumentException(
                        $"{name} should be in " +
                        $"{(includeMin ? "[" : "(")}" +
                        $"{min}, {max}" +
                        $"{(includeMax ? "]" : ")")}");
        };
        validateRangeFunc(nameof(temperature), temperature, 0, 1, true, true);
        validateRangeFunc(nameof(topP), topP, 0, 1, false, false);
        validateRangeFunc(nameof(n), n, 1, 5, true, true);
        validateRangeFunc(nameof(presencePenalty), presencePenalty, -2, 2, true, true);
        validateRangeFunc(nameof(frequencyPenalty), frequencyPenalty, -2, 2, true, true);
        if (stop != null)
        {
            if (stop.Count > 5)
                throw new ArgumentException("stop token count is at most 5");
            foreach (var token in stop)
            {
                var encoded = Encoding.UTF8.GetBytes(token);
                if (encoded.Length > 32)
                    throw new ArgumentException($"stop token [{token}] is too long, max length of it is 32B");
            }
        }
        if (stream != null && stream.HasValue && stream.Value)
            throw new NotImplementedException("streaming is not supported yet");

        var json = new JsonObject();
        json["model"] = model;

        var messagesArray = new JsonArray();
        foreach (var message in messages)
            messagesArray.Add(message);
        json["messages"] = messagesArray;

        if (maxTokens != null && maxTokens.HasValue)
            json["max_tokens"] = maxTokens;
        if (temperature != null && temperature.HasValue)
            json["temperature"] = temperature;
        if (topP != null && topP.HasValue)
            json["top_p"] = topP;
        if (n != null && n.HasValue)
            json["n"] = n;
        if (presencePenalty != null && presencePenalty.HasValue)
            json["presence_penalty"] = presencePenalty;
        if (frequencyPenalty != null && frequencyPenalty.HasValue)
            json["frequency_penalty"] = frequencyPenalty;
        if (responseFormat != null && responseFormat.HasValue)
            json["response_format"] = responseFormat.Value.GetResponseFormatJson();

        if (stop != null)
        {
            var stopArray = new JsonArray();
            foreach (var token in stop)
                stopArray.Add(token);
            json["stop"] = stopArray;
        }
        if (stream != null && stream.HasValue)
            json["stream"] = stream.Value;

        var response = await HTTPPostFromJsonAsync<ChatResponse>("https://api.moonshot.cn/v1/chat/completions", json.ToJsonString(serializerOptions), key, cancellationToken);
        var betterChoices = response!.choices!.Where(choice => choice.finish_reason == "stop").ToArray();
        if (betterChoices.Length > 0)
            return betterChoices[Random.Shared.Next(betterChoices.Length)].message;
        return response.choices![Random.Shared.Next(response.choices.Length)].message;
    }
}
public static partial class Moonshot
{
    private static readonly HttpClient client = new();
    private static readonly JsonSerializerOptions serializerOptions = new(JsonSerializerOptions.Default)
    {
        IncludeFields = true,
        WriteIndented = true,
    };
    private static readonly Error[] errors = [
        new(400, "content_filter", "The request was rejected because it was considered high risk", "内容审查拒绝，您的输入或生成内容可能包含不安全或敏感内容，请您避免输入易产生敏感内容的提示语，谢谢"),
        new(400, "invalid_request_error", "Invalid request: {error_details}", "请求无效，通常是您请求格式错误或者缺少必要参数，请检查后重试"),
        new(400, "invalid_request_error", "Input token length too long", "请求中的 tokens 长度过长，请求不要超过模型 tokens 的最长限制"),
        new(400, "invalid_request_error", "Your request exceeded model token limit : {max_model_length}", "请求的 tokens 数和设置的 max_tokens 加和超过了模型规格长度，请检查请求体的规格或选择合适长度的模型"),
        new(400, "invalid_request_error", "Invalid purpose: only 'file-extract' accepted", "请求中的目的（purpose）不正确，当前只接受 'file-extract'，请修改后重新请求"),
        new(400, "invalid_request_error", "File size is too large, max file size is 100MB, please confirm and re-upload the file", "上传的文件大小超过了限制，请重新上传"),
        new(400, "invalid_request_error", "File size is zero, please confirm and re-upload the file", "上传的文件大小为 0，请重新上传"),
        new(400, "invalid_request_error", "The number of files you have uploaded exceeded the max file count {max_file_count}, please delete previous uploaded files", "上传的文件总数超限，请删除不用的早期的文件后重新 上传"),
        new(401, "invalid_authentication_error", "Invalid Authentication", "鉴权失败，请检查 apikey 是否正确，请修改后重试"),
        new(401, "invalid_authentication_error", "Incorrect API key provided", "鉴权失败，请检查 apikey 是否提供以及 apikey 是否正确，请修改后重试"),
        new(403, "exceeded_current_quota_error", "Your account {uid}<{ak-id}> is not active, current state: {current state}, you may consider to check your account balance", "账户异常，请检查您的账户余额"),
        new(403, "permission_denied_error", "The API you are accessing is not open", "访问的 API 暂未开放"),
        new(403, "permission_denied_error", "You are not allowed to get other user info", "访问其他用户信息的行为不被允许，请检查"),
        new(404, "resource_not_found_error", "Not found the model or Permission denied", "不存在此模型或者没有授权访问此模型，请检查后重试"),
        new(404, "resource_not_found_error", "Users {user_id} not found", "找不到该用户，请检查后重试"),
        new(429, "engine_overloaded_error", "The engine is currently overloaded, please try again later", "当前并发请求过多，节点限流中，请稍后重试；建议充值升级 tier，享受更丝滑的体验"),
        new(429, "exceeded_current_quota_error", "You exceeded your current token quota: {token_credit}, please check your account balance", "账户额度不足，请检查账户余额，保证账户余额可匹配您 tokens 的消耗费用后重试"),
        new(429, "rate_limit_reached_error", "Your account {uid}<{ak-id}> request reached max concurrency: {Concurrency}, please try again after {time} seconds", "请求触发了账户并发个数的限制，请等待指定时间后重试"),
        new(429, "rate_limit_reached_error", "Your account {uid}<{ak-id}> request reached max request: {RPM}, please try again after {time} seconds", "请求触发了账户 RPM 速率限制，请等待指定时间后重试"),
        new(429, "rate_limit_reached_error", "Your account {uid}<{ak-id}> request reached TPM rate limit, current:{current_tpm}, limit:{max_tpm}", "请求触发了账户 TPM 速率限制，请等待指定时间后重试"),
        new(429, "rate_limit_reached_error", "Your account {uid}<{ak-id}> request reached TPD rate limit,current:{current_tpd}, limit:{max_tpd}", "请求触发了账户 TPD 速率限制，请等待指定时间后重试"),
        new(500, "server_error", "Failed to extract file: {error}", "解析文件失败，请重试"),
        new(500, "unexpected_output", "invalid state transition", "内部错误，请联系管理员")
    ];

    internal static async Task<HttpResponseMessage> HTTPGetAsync(string url, string key, CancellationToken cancellationToken = default)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return await client.GetAsync(url, cancellationToken);
    }
    internal static async Task<HttpResponseMessage> HTTPPostAsync(string url, string json, string key, CancellationToken cancellationToken = default)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
        return await client.PostAsync(url, content, cancellationToken);
    }
    internal static async Task<HttpResponseMessage> HTTPPostAsync<T>(string url, T value, string key, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(value, serializerOptions);
        return await HTTPPostAsync(url, json, key, cancellationToken);
    }
    internal static async Task<Exception> HTTPHandleErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken = default)
    {
        var jsonString = await response.Content.ReadAsStringAsync(cancellationToken);
        var root = JsonNode.Parse(jsonString)!["error"];
        if (root is not JsonObject)
            return new(new(-1, "unknown", jsonString, "未知错误"));
        var code = (int)response.StatusCode;
        var type = root!["type"]?.GetValue<string>() ?? string.Empty;
        var message = root["message"]!.GetValue<string>() ?? string.Empty;
        var e = new Error(code, type, message, string.Empty);
        var similarErrors = errors.Where(error => Error.GetSimilarity(error, e) > 0).ToList();
        if (similarErrors.Count == 0)
            return new(new(-1, "unknown", "unknown error", "未知错误"));
        similarErrors.Sort((a, b) => Error.GetSimilarity(b, e).CompareTo(Error.GetSimilarity(a, e)));
        e.description = similarErrors[0].description;
        return new(e);
    }

    internal static async Task<T?> HTTPGetFromJsonAsync<T>(string url, string key, CancellationToken cancellationToken = default)
    {
        var response = await HTTPGetAsync(url, key, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw await HTTPHandleErrorAsync(response, cancellationToken);
        var jsonString = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<T>(jsonString, serializerOptions);
    }
    internal static async Task<TOut?> HTTPPostFromJsonAsync<TOut>(string url, string json, string key, CancellationToken cancellationToken = default)
    {
        var response = await HTTPPostAsync(url, json, key, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw await HTTPHandleErrorAsync(response, cancellationToken);
        var jsonString = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<TOut>(jsonString, serializerOptions);
    }
    internal static async Task<TOut?> HTTPPostFromJsonAsync<TIn, TOut>(string url, TIn value, string key, CancellationToken cancellationToken = default)
    {
        var response = await HTTPPostAsync(url, value, key, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw await HTTPHandleErrorAsync(response, cancellationToken);
        var jsonString = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<TOut>(jsonString, serializerOptions);
    }
    internal static async Task<Model[]> ListModelsAsync(string key, CancellationToken cancellationToken = default)
    {
        var response = await HTTPGetAsync("https://api.moonshot.cn/v1/models", key, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw await HTTPHandleErrorAsync(response, cancellationToken);
        var list = await response.Content.ReadFromJsonAsync<ModelList>(serializerOptions, cancellationToken);
        return list!.data ?? [];
    }

    internal static JsonObject GetResponseFormatJson(this ResponseFormat format) => format switch
    {
        ResponseFormat.Text => new()
        {
            ["type"] = "text"
        },
        ResponseFormat.JsonObject => new()
        {
            ["type"] = "json_object"
        },
        _ => throw new NotImplementedException()
    };

    internal class Permission
    {
        public int created;
        public string? id;
        public string? @object;
        public bool allow_create_engine;
        public bool allow_sampling;
        public bool allow_logprobs;
        public bool allow_search_indices;
        public bool allow_view;
        public bool allow_fine_tuning;
        public string? organization;
        public string? @group;
        public bool is_blocking;
    }
    internal class Model
    {
        public int created;
        public string? id;
        public string? @object;
        public string? owned_by;
        public Permission[]? permission;
        public string? root;
        public string? parent;
    }
    internal class ModelList
    {
        public string? @object;
        public Model[]? data;
    }
    internal class BalanceResponse
    {
        public int code;
        public Balance data;
        public string? scode;
        public bool status;
    }
    internal class Choice
    {
        public int index;
        public Message message;
        public string? finish_reason;
    }
    internal class Usage
    {
        public int prompt_tokens;
        public int completion_tokens;
        public int total_tokens;
    }
    internal class ChatResponse
    {
        public string? id;
        public string? @object;
        public int created;
        public string? model;
        public Choice[]? choices;
        public Usage? usage;
    }
}