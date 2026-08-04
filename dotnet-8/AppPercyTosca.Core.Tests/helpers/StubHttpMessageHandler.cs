using System.Net;

namespace AppPercyTosca.Core.Tests
{
    /// <summary>
    /// Serves canned responses per endpoint so the CLI client can be exercised without a running
    /// Percy CLI, and records what was sent so tests can assert on the request bodies.
    /// </summary>
    public class StubHttpMessageHandler : HttpMessageHandler
    {
        public class Reply
        {
            public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
            public string Body { get; set; } = "{}";
            public string? CoreVersion { get; set; } = "1.27.0";
        }

        public record Recorded(string Method, string Url, string? Body);

        private readonly Dictionary<string, Queue<Reply>> _replies =
            new Dictionary<string, Queue<Reply>>();
        private Reply _fallback = new Reply();

        public List<Recorded> Requests { get; } = new List<Recorded>();

        /// <summary>Queues a reply for requests whose path ends with <paramref name="endpoint"/>.</summary>
        public StubHttpMessageHandler On(string endpoint, string body,
            HttpStatusCode status = HttpStatusCode.OK, string? coreVersion = "1.27.0")
        {
            if (!_replies.TryGetValue(endpoint, out Queue<Reply>? queue))
            {
                queue = new Queue<Reply>();
                _replies[endpoint] = queue;
            }
            queue.Enqueue(new Reply { Body = body, Status = status, CoreVersion = coreVersion });
            return this;
        }

        /// <summary>Reply used for any endpoint with nothing queued.</summary>
        public StubHttpMessageHandler Default(string body,
            HttpStatusCode status = HttpStatusCode.OK, string? coreVersion = "1.27.0")
        {
            _fallback = new Reply { Body = body, Status = status, CoreVersion = coreVersion };
            return this;
        }

        public string? BodyFor(string endpoint) =>
            Requests.LastOrDefault(r => r.Url.EndsWith(endpoint, StringComparison.Ordinal))?.Body;

        public int CountFor(string endpoint) =>
            Requests.Count(r => r.Url.EndsWith(endpoint, StringComparison.Ordinal));

        public HttpClient Client() => new HttpClient(this);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.ToString();
            string? body = request.Content?.ReadAsStringAsync(cancellationToken).Result;
            Requests.Add(new Recorded(request.Method.Method, url, body));

            Reply reply = _fallback;
            foreach (KeyValuePair<string, Queue<Reply>> entry in _replies)
            {
                if (url.EndsWith(entry.Key, StringComparison.Ordinal) && entry.Value.Count > 0)
                {
                    reply = entry.Value.Dequeue();
                    break;
                }
            }

            HttpResponseMessage response = new HttpResponseMessage(reply.Status)
            {
                Content = new StringContent(reply.Body)
            };
            if (reply.CoreVersion != null)
            {
                response.Headers.Add("x-percy-core-version", reply.CoreVersion);
            }
            return Task.FromResult(response);
        }
    }
}
