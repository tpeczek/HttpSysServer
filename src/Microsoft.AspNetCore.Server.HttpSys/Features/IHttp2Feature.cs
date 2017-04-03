using Microsoft.AspNetCore.Http;

namespace Microsoft.AspNetCore.Http.Features
{
    public interface IHttp2Feature
    {
        /// <summary>
        /// Indicates if this request is served over HTTP/2.
        /// </summary>
        bool IsHttp2Request { get; }

        /// <summary>
        /// This API is to support applications sending push promises to HTTP 2.0 clients.
        /// </summary>
        /// <param name="path">The URL of the push request. It should be the relative path to the resource that the server wants to push to the client.</param>
        /// <remarks>
        /// PushPromise is non-deterministic and applications shouldn't have logic that depends on it. Its only purpose is performance advantage in some cases. There are many conditions (protocol and implementation) that may cause to ignore the push requests completely. The expectation is based on fire-and-forget.
        /// </remarks>
        void PushPromise(string path);

        /// <summary>
        /// This API is to support applications sending push promises to HTTP 2.0 clients.
        /// </summary>
        /// <param name="path">The URL of the push request. It should be the relative path to the resource that the server wants to push to the client.</param>
        /// <param name="method">Http request method that would be used by the push request.</param>
        /// <param name="headers">Http request headers that would be used by the push request.</param>
        /// <remarks>
        /// PushPromise is non-deterministic and applications shouldn't have logic that depends on it. Its only purpose is performance advantage in some cases. There are many conditions (protocol and implementation) that may cause to ignore the push requests completely. The expectation is based on fire-and-forget.
        /// </remarks>
        void PushPromise(string path, string method, IHeaderDictionary headers);
    }
}
