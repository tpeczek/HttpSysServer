// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using static Microsoft.AspNetCore.Server.HttpSys.UnsafeNclNativeMethods;

namespace Microsoft.AspNetCore.Server.HttpSys
{
    internal sealed class Response
    {
        private ResponseState _responseState;
        private string _reasonPhrase;
        private ResponseBody _nativeStream;
        private AuthenticationSchemes _authChallenges;
        private TimeSpan? _cacheTtl;
        private long _expectedBodyLength;
        private BoundaryType _boundaryType;
        private HttpApi.HTTP_RESPONSE_V2 _nativeResponse;

        internal Response(RequestContext requestContext)
        {
            // TODO: Verbose log
            RequestContext = requestContext;
            Headers = new HeaderCollection();
            // We haven't started yet, or we're just buffered, we can clear any data, headers, and state so
            // that we can start over (e.g. to write an error message).
            _nativeResponse = new HttpApi.HTTP_RESPONSE_V2();
            Headers.IsReadOnly = false;
            Headers.Clear();
            _reasonPhrase = null;
            _boundaryType = BoundaryType.None;
            _nativeResponse.Response_V1.StatusCode = (ushort)StatusCodes.Status200OK;
            _nativeResponse.Response_V1.Version.MajorVersion = 1;
            _nativeResponse.Response_V1.Version.MinorVersion = 1;
            _responseState = ResponseState.Created;
            _expectedBodyLength = 0;
            _nativeStream = null;
            _cacheTtl = null;
            _authChallenges = RequestContext.Server.Options.Authentication.Schemes;
        }

        private enum ResponseState
        {
            Created,
            ComputedHeaders,
            Started,
            Closed,
        }

        private RequestContext RequestContext { get; }

        private Request Request => RequestContext.Request;

        public int StatusCode
        {
            get { return _nativeResponse.Response_V1.StatusCode; }
            set
            {
                // Http.Sys automatically sends 100 Continue responses when you read from the request body.
                if (value <= 100 || 999 < value)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), value, string.Format(Resources.Exception_InvalidStatusCode, value));
                }
                CheckResponseStarted();
                _nativeResponse.Response_V1.StatusCode = (ushort)value;
            }
        }

        public string ReasonPhrase
        {
            get { return _reasonPhrase; }
            set
            {
                // TODO: Validate user input for illegal chars, length limit, etc.?
                CheckResponseStarted();
                _reasonPhrase = value;
            }
        }

        public Stream Body
        {
            get
            {
                EnsureResponseStream();
                return _nativeStream;
            }
        }

        internal bool BodyIsFinished => _nativeStream?.IsDisposed ?? _responseState >= ResponseState.Closed;

        /// <summary>
        /// The authentication challenges that will be added to the response if the status code is 401.
        /// This must be a subset of the AuthenticationSchemes enabled on the server.
        /// </summary>
        public AuthenticationSchemes AuthenticationChallenges
        {
            get { return _authChallenges; }
            set
            {
                CheckResponseStarted();
                _authChallenges = value;
            }
        }

        private string GetReasonPhrase(int statusCode)
        {
            string reasonPhrase = ReasonPhrase;
            if (string.IsNullOrWhiteSpace(reasonPhrase))
            {
                // If the user hasn't set this then it is generated on the fly if possible.
                reasonPhrase = HttpReasonPhrase.Get(statusCode) ?? string.Empty;
            }
            return reasonPhrase;
        }

        // We MUST NOT send message-body when we send responses with these Status codes
        private static readonly int[] StatusWithNoResponseBody = { 100, 101, 204, 205, 304 };

        private static bool CanSendResponseBody(int responseCode)
        {
            for (int i = 0; i < StatusWithNoResponseBody.Length; i++)
            {
                if (responseCode == StatusWithNoResponseBody[i])
                {
                    return false;
                }
            }
            return true;
        }

        public HeaderCollection Headers { get; }

        internal long ExpectedBodyLength
        {
            get { return _expectedBodyLength; }
        }

        // Header accessors
        public long? ContentLength
        {
            get { return Headers.ContentLength; }
            set { Headers.ContentLength = value; }
        }

        /// <summary>
        /// Enable kernel caching for the response with the given timeout. Http.Sys determines if the response
        /// can be cached.
        /// </summary>
        public TimeSpan? CacheTtl
        {
            get { return _cacheTtl; }
            set
            {
                CheckResponseStarted();
                _cacheTtl = value;
            }
        }

        internal void Abort()
        {
            // Update state for HasStarted. Do not attempt a graceful Dispose.
            _responseState = ResponseState.Closed;
        }

        // should only be called from RequestContext
        internal void Dispose()
        {
            if (_responseState >= ResponseState.Closed)
            {
                return;
            }
            // TODO: Verbose log
            EnsureResponseStream();
            _nativeStream.Dispose();
            _responseState = ResponseState.Closed;
        }

        internal BoundaryType BoundaryType
        {
            get { return _boundaryType; }
        }

        internal bool HasComputedHeaders
        {
            get { return _responseState >= ResponseState.ComputedHeaders; }
        }

        /// <summary>
        /// Indicates if the response status, reason, and headers are prepared to send and can
        /// no longer be modified. This is caused by the first write or flush to the response body.
        /// </summary>
        public bool HasStarted
        {
            get { return _responseState >= ResponseState.Started; }
        }

        private void CheckResponseStarted()
        {
            if (HasStarted)
            {
                throw new InvalidOperationException("Headers already sent.");
            }
        }

        private void EnsureResponseStream()
        {
            if (_nativeStream == null)
            {
                _nativeStream = new ResponseBody(RequestContext);
            }
        }

        /*
        12.3
        HttpSendHttpResponse() and HttpSendResponseEntityBody() Flag Values.
        The following flags can be used on calls to HttpSendHttpResponse() and HttpSendResponseEntityBody() API calls:

        #define HTTP_SEND_RESPONSE_FLAG_DISCONNECT          0x00000001
        #define HTTP_SEND_RESPONSE_FLAG_MORE_DATA           0x00000002
        #define HTTP_SEND_RESPONSE_FLAG_RAW_HEADER          0x00000004
        #define HTTP_SEND_RESPONSE_FLAG_VALID               0x00000007

        HTTP_SEND_RESPONSE_FLAG_DISCONNECT:
            specifies that the network connection should be disconnected immediately after
            sending the response, overriding the HTTP protocol's persistent connection features.
        HTTP_SEND_RESPONSE_FLAG_MORE_DATA:
            specifies that additional entity body data will be sent by the caller. Thus,
            the last call HttpSendResponseEntityBody for a RequestId, will have this flag reset.
        HTTP_SEND_RESPONSE_RAW_HEADER:
            specifies that a caller of HttpSendResponseEntityBody() is intentionally omitting
            a call to HttpSendHttpResponse() in order to bypass normal header processing. The
            actual HTTP header will be generated by the application and sent as entity body.
            This flag should be passed on the first call to HttpSendResponseEntityBody, and
            not after. Thus, flag is not applicable to HttpSendHttpResponse.
        */

        // TODO: Consider using HTTP_SEND_RESPONSE_RAW_HEADER with HttpSendResponseEntityBody instead of calling HttpSendHttpResponse.
        // This will give us more control of the bytes that hit the wire, including encodings, HTTP 1.0, etc..
        // It may also be faster to do this work in managed code and then pass down only one buffer.
        // What would we loose by bypassing HttpSendHttpResponse?
        //
        // TODO: Consider using the HTTP_SEND_RESPONSE_FLAG_BUFFER_DATA flag for most/all responses rather than just Opaque.
        internal unsafe uint SendHeaders(HttpApi.HTTP_DATA_CHUNK[] dataChunks,
            ResponseStreamAsyncResult asyncResult,
            HttpApi.HTTP_FLAGS flags,
            bool isOpaqueUpgrade)
        {
            Debug.Assert(!HasStarted, "HttpListenerResponse::SendHeaders()|SentHeaders is true.");

            _responseState = ResponseState.Started;
            var reasonPhrase = GetReasonPhrase(StatusCode);

            uint statusCode;
            uint bytesSent;
            List<GCHandle> pinnedHeaders = SerializeHeaders(isOpaqueUpgrade);
            try
            {
                if (dataChunks != null)
                {
                    if (pinnedHeaders == null)
                    {
                        pinnedHeaders = new List<GCHandle>();
                    }
                    var handle = GCHandle.Alloc(dataChunks, GCHandleType.Pinned);
                    pinnedHeaders.Add(handle);
                    _nativeResponse.Response_V1.EntityChunkCount = (ushort)dataChunks.Length;
                    _nativeResponse.Response_V1.pEntityChunks = (HttpApi.HTTP_DATA_CHUNK*)handle.AddrOfPinnedObject();
                }
                else if (asyncResult != null && asyncResult.DataChunks != null)
                {
                    _nativeResponse.Response_V1.EntityChunkCount = asyncResult.DataChunkCount;
                    _nativeResponse.Response_V1.pEntityChunks = asyncResult.DataChunks;
                }
                else
                {
                    _nativeResponse.Response_V1.EntityChunkCount = 0;
                    _nativeResponse.Response_V1.pEntityChunks = null;
                }

                var cachePolicy = new HttpApi.HTTP_CACHE_POLICY();
                if (_cacheTtl.HasValue && _cacheTtl.Value > TimeSpan.Zero)
                {
                    cachePolicy.Policy = HttpApi.HTTP_CACHE_POLICY_TYPE.HttpCachePolicyTimeToLive;
                    cachePolicy.SecondsToLive = (uint)Math.Min(_cacheTtl.Value.Ticks / TimeSpan.TicksPerSecond, Int32.MaxValue);
                }

                byte[] reasonPhraseBytes = HeaderEncoding.GetBytes(reasonPhrase);
                fixed (byte* pReasonPhrase = reasonPhraseBytes)
                {
                    _nativeResponse.Response_V1.ReasonLength = (ushort)reasonPhraseBytes.Length;
                    _nativeResponse.Response_V1.pReason = (sbyte*)pReasonPhrase;
                    fixed (HttpApi.HTTP_RESPONSE_V2* pResponse = &_nativeResponse)
                    {
                        statusCode =
                            HttpApi.HttpSendHttpResponse(
                                RequestContext.Server.RequestQueue.Handle,
                                Request.RequestId,
                                (uint)flags,
                                pResponse,
                                &cachePolicy,
                                &bytesSent,
                                IntPtr.Zero,
                                0,
                                asyncResult == null ? SafeNativeOverlapped.Zero : asyncResult.NativeOverlapped,
                                IntPtr.Zero);

                        if (asyncResult != null &&
                            statusCode == ErrorCodes.ERROR_SUCCESS &&
                            HttpSysListener.SkipIOCPCallbackOnSuccess)
                        {
                            asyncResult.BytesSent = bytesSent;
                            // The caller will invoke IOCompleted
                        }
                    }
                }
            }
            finally
            {
                FreePinnedHeaders(pinnedHeaders);
            }
            return statusCode;
        }

        internal HttpApi.HTTP_FLAGS ComputeHeaders(long writeCount, bool endOfRequest = false)
        {
            if (StatusCode == (ushort)StatusCodes.Status401Unauthorized)
            {
                RequestContext.Server.Options.Authentication.SetAuthenticationChallenge(RequestContext);
            }

            var flags = HttpApi.HTTP_FLAGS.NONE;
            Debug.Assert(!HasComputedHeaders, nameof(HasComputedHeaders) + " is true.");
            _responseState = ResponseState.ComputedHeaders;

            // Gather everything from the request that affects the response:
            var requestVersion = Request.ProtocolVersion;
            var requestConnectionString = Request.Headers[HttpKnownHeaderNames.Connection];
            var isHeadRequest = Request.IsHeadMethod;
            var requestCloseSet = Matches(Constants.Close, requestConnectionString);

            // Gather everything the app may have set on the response:
            // Http.Sys does not allow us to specify the response protocol version, assume this is a HTTP/1.1 response when making decisions.
            var responseConnectionString = Headers[HttpKnownHeaderNames.Connection];
            var transferEncodingString = Headers[HttpKnownHeaderNames.TransferEncoding];
            var responseContentLength = ContentLength;
            var responseCloseSet = Matches(Constants.Close, responseConnectionString);
            var responseChunkedSet = Matches(Constants.Chunked, transferEncodingString);
            var statusCanHaveBody = CanSendResponseBody(RequestContext.Response.StatusCode);

            // Determine if the connection will be kept alive or closed.
            var keepConnectionAlive = true;
            if (requestVersion <= Constants.V1_0 // Http.Sys does not support "Keep-Alive: true" or "Connection: Keep-Alive"
                || (requestVersion == Constants.V1_1 && requestCloseSet)
                || responseCloseSet)
            {
                keepConnectionAlive = false;
            }

            // Determine the body format. If the user asks to do something, let them, otherwise choose a good default for the scenario.
            if (responseContentLength.HasValue)
            {
                _boundaryType = BoundaryType.ContentLength;
                // ComputeLeftToWrite checks for HEAD requests when setting _leftToWrite
                _expectedBodyLength = responseContentLength.Value;
                if (_expectedBodyLength == writeCount && !isHeadRequest)
                {
                    // A single write with the whole content-length. Http.Sys will set the content-length for us in this scenario.
                    // If we don't remove it then range requests served from cache will have two.
                    // https://github.com/aspnet/HttpSysServer/issues/167
                    ContentLength = null;
                }
            }
            else if (responseChunkedSet)
            {
                // The application is performing it's own chunking.
                _boundaryType = BoundaryType.PassThrough;
            }
            else if (endOfRequest && !(isHeadRequest && statusCanHaveBody)) // HEAD requests should always end without a body. Assume a GET response would have a body.
            {
                if (statusCanHaveBody)
                {
                    Headers[HttpKnownHeaderNames.ContentLength] = Constants.Zero;
                }
                _boundaryType = BoundaryType.ContentLength;
                _expectedBodyLength = 0;
            }
            else if (requestVersion == Constants.V1_1)
            {
                _boundaryType = BoundaryType.Chunked;
                Headers[HttpKnownHeaderNames.TransferEncoding] = Constants.Chunked;
            }
            else
            {
                // v1.0 and the length cannot be determined, so we must close the connection after writing data
                keepConnectionAlive = false;
                _boundaryType = BoundaryType.Close;
            }

            // Managed connection lifetime
            if (!keepConnectionAlive)
            {
                // All Http.Sys responses are v1.1, so use 1.1 response headers
                // Note that if we don't add this header, Http.Sys will often do it for us.
                if (!responseCloseSet)
                {
                    Headers.Append(HttpKnownHeaderNames.Connection, Constants.Close);
                }
                flags = HttpApi.HTTP_FLAGS.HTTP_SEND_RESPONSE_FLAG_DISCONNECT;
            }

            return flags;
        }

        private static bool Matches(string knownValue, string input)
        {
            return string.Equals(knownValue, input?.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private unsafe List<GCHandle> SerializeHeaders(bool isOpaqueUpgrade)
        {
            Headers.IsReadOnly = true; // Prohibit further modifications.
            HttpApi.HTTP_UNKNOWN_HEADER[] unknownHeaders = null;
            HttpApi.HTTP_RESPONSE_INFO[] knownHeaderInfo = null;
            List<GCHandle> pinnedHeaders;
            GCHandle gcHandle;

            if (Headers.Count == 0)
            {
                return null;
            }
            string headerName;
            string headerValue;
            int lookup;
            byte[] bytes = null;
            pinnedHeaders = new List<GCHandle>();

            int numUnknownHeaders = 0;
            int numKnownMultiHeaders = 0;
            foreach (var headerPair in Headers)
            {
                if (headerPair.Value.Count == 0)
                {
                    continue;
                }
                // See if this is an unknown header
                lookup = HttpApi.HTTP_HEADER_ID.IndexOfKnownHeader(headerPair.Key);

                // Http.Sys doesn't let us send the Connection: Upgrade header as a Known header.
                if (lookup == -1 ||
                    (isOpaqueUpgrade && lookup == (int)HttpApi.HTTP_RESPONSE_HEADER_ID.HttpHeaderConnection))
                {
                    numUnknownHeaders += headerPair.Value.Count;
                }
                else if (headerPair.Value.Count > 1)
                {
                    numKnownMultiHeaders++;
                }
                // else known single-value header.
            }

            try
            {
                fixed (HttpApi.HTTP_KNOWN_HEADER* pKnownHeaders = &_nativeResponse.Response_V1.Headers.KnownHeaders)
                {
                    foreach (var headerPair in Headers)
                    {
                        if (headerPair.Value.Count == 0)
                        {
                            continue;
                        }
                        headerName = headerPair.Key;
                        StringValues headerValues = headerPair.Value;
                        lookup = HttpApi.HTTP_HEADER_ID.IndexOfKnownHeader(headerName);

                        // Http.Sys doesn't let us send the Connection: Upgrade header as a Known header.
                        if (lookup == -1 ||
                            (isOpaqueUpgrade && lookup == (int)HttpApi.HTTP_RESPONSE_HEADER_ID.HttpHeaderConnection))
                        {
                            if (unknownHeaders == null)
                            {
                                unknownHeaders = new HttpApi.HTTP_UNKNOWN_HEADER[numUnknownHeaders];
                                gcHandle = GCHandle.Alloc(unknownHeaders, GCHandleType.Pinned);
                                pinnedHeaders.Add(gcHandle);
                                _nativeResponse.Response_V1.Headers.pUnknownHeaders = (HttpApi.HTTP_UNKNOWN_HEADER*)gcHandle.AddrOfPinnedObject();
                            }

                            for (int headerValueIndex = 0; headerValueIndex < headerValues.Count; headerValueIndex++)
                            {
                                // Add Name
                                bytes = HeaderEncoding.GetBytes(headerName);
                                unknownHeaders[_nativeResponse.Response_V1.Headers.UnknownHeaderCount].NameLength = (ushort)bytes.Length;
                                gcHandle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                                pinnedHeaders.Add(gcHandle);
                                unknownHeaders[_nativeResponse.Response_V1.Headers.UnknownHeaderCount].pName = (sbyte*)gcHandle.AddrOfPinnedObject();

                                // Add Value
                                headerValue = headerValues[headerValueIndex] ?? string.Empty;
                                bytes = HeaderEncoding.GetBytes(headerValue);
                                unknownHeaders[_nativeResponse.Response_V1.Headers.UnknownHeaderCount].RawValueLength = (ushort)bytes.Length;
                                gcHandle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                                pinnedHeaders.Add(gcHandle);
                                unknownHeaders[_nativeResponse.Response_V1.Headers.UnknownHeaderCount].pRawValue = (sbyte*)gcHandle.AddrOfPinnedObject();
                                _nativeResponse.Response_V1.Headers.UnknownHeaderCount++;
                            }
                        }
                        else if (headerPair.Value.Count == 1)
                        {
                            headerValue = headerValues[0] ?? string.Empty;
                            bytes = HeaderEncoding.GetBytes(headerValue);
                            pKnownHeaders[lookup].RawValueLength = (ushort)bytes.Length;
                            gcHandle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                            pinnedHeaders.Add(gcHandle);
                            pKnownHeaders[lookup].pRawValue = (sbyte*)gcHandle.AddrOfPinnedObject();
                        }
                        else
                        {
                            if (knownHeaderInfo == null)
                            {
                                knownHeaderInfo = new HttpApi.HTTP_RESPONSE_INFO[numKnownMultiHeaders];
                                gcHandle = GCHandle.Alloc(knownHeaderInfo, GCHandleType.Pinned);
                                pinnedHeaders.Add(gcHandle);
                                _nativeResponse.pResponseInfo = (HttpApi.HTTP_RESPONSE_INFO*)gcHandle.AddrOfPinnedObject();
                            }

                            knownHeaderInfo[_nativeResponse.ResponseInfoCount].Type = HttpApi.HTTP_RESPONSE_INFO_TYPE.HttpResponseInfoTypeMultipleKnownHeaders;
                            knownHeaderInfo[_nativeResponse.ResponseInfoCount].Length = (uint)Marshal.SizeOf<HttpApi.HTTP_MULTIPLE_KNOWN_HEADERS>();

                            HttpApi.HTTP_MULTIPLE_KNOWN_HEADERS header = new HttpApi.HTTP_MULTIPLE_KNOWN_HEADERS();

                            header.HeaderId = (HttpApi.HTTP_RESPONSE_HEADER_ID)lookup;
                            header.Flags = HttpApi.HTTP_RESPONSE_INFO_FLAGS.PreserveOrder; // TODO: The docs say this is for www-auth only.

                            HttpApi.HTTP_KNOWN_HEADER[] nativeHeaderValues = new HttpApi.HTTP_KNOWN_HEADER[headerValues.Count];
                            gcHandle = GCHandle.Alloc(nativeHeaderValues, GCHandleType.Pinned);
                            pinnedHeaders.Add(gcHandle);
                            header.KnownHeaders = (HttpApi.HTTP_KNOWN_HEADER*)gcHandle.AddrOfPinnedObject();

                            for (int headerValueIndex = 0; headerValueIndex < headerValues.Count; headerValueIndex++)
                            {
                                // Add Value
                                headerValue = headerValues[headerValueIndex] ?? string.Empty;
                                bytes = HeaderEncoding.GetBytes(headerValue);
                                nativeHeaderValues[header.KnownHeaderCount].RawValueLength = (ushort)bytes.Length;
                                gcHandle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                                pinnedHeaders.Add(gcHandle);
                                nativeHeaderValues[header.KnownHeaderCount].pRawValue = (sbyte*)gcHandle.AddrOfPinnedObject();
                                header.KnownHeaderCount++;
                            }

                            // This type is a struct, not an object, so pinning it causes a boxed copy to be created. We can't do that until after all the fields are set.
                            gcHandle = GCHandle.Alloc(header, GCHandleType.Pinned);
                            pinnedHeaders.Add(gcHandle);
                            knownHeaderInfo[_nativeResponse.ResponseInfoCount].pInfo = (HttpApi.HTTP_MULTIPLE_KNOWN_HEADERS*)gcHandle.AddrOfPinnedObject();

                            _nativeResponse.ResponseInfoCount++;
                        }
                    }
                }
            }
            catch
            {
                FreePinnedHeaders(pinnedHeaders);
                throw;
            }
            return pinnedHeaders;
        }

        private static void FreePinnedHeaders(List<GCHandle> pinnedHeaders)
        {
            if (pinnedHeaders != null)
            {
                foreach (GCHandle gcHandle in pinnedHeaders)
                {
                    if (gcHandle.IsAllocated)
                    {
                        gcHandle.Free();
                    }
                }
            }
        }

        // Subset of ComputeHeaders
        internal void SendOpaqueUpgrade()
        {
            _boundaryType = BoundaryType.Close;

            // TODO: Send headers async?
            ulong errorCode = SendHeaders(null, null,
                HttpApi.HTTP_FLAGS.HTTP_SEND_RESPONSE_FLAG_OPAQUE |
                HttpApi.HTTP_FLAGS.HTTP_SEND_RESPONSE_FLAG_MORE_DATA |
                HttpApi.HTTP_FLAGS.HTTP_SEND_RESPONSE_FLAG_BUFFER_DATA,
                true);

            if (errorCode != ErrorCodes.ERROR_SUCCESS)
            {
                throw new HttpSysException((int)errorCode);
            }
        }

        internal void CancelLastWrite()
        {
            _nativeStream?.CancelLastWrite();
        }

        public Task SendFileAsync(string path, long offset, long? count, CancellationToken cancel)
        {
            EnsureResponseStream();
            return _nativeStream.SendFileAsync(path, offset, count, cancel);
        }

        internal void SwitchToOpaqueMode()
        {
            EnsureResponseStream();
            _nativeStream.SwitchToOpaqueMode();
        }

        internal unsafe void PushPromise(string path, string method, IDictionary<string, StringValues> headers)
        {
            if (Request.IsHttp2)
            {
                uint statusCode = ErrorCodes.ERROR_SUCCESS;
                HttpApi.HTTP_VERB verb = HttpApi.HTTP_VERB.HttpVerbHEAD;
                string query = null;
                HttpApi.HTTP_REQUEST_HEADERS* nativeHeadersPointer = null;

                string methodToUpper = method.ToUpperInvariant();
                if (HttpApi.HttpVerbs[(int)HttpApi.HTTP_VERB.HttpVerbGET] == methodToUpper)
                {
                    verb = HttpApi.HTTP_VERB.HttpVerbGET;
                }
                else if (HttpApi.HttpVerbs[(int)HttpApi.HTTP_VERB.HttpVerbHEAD] != methodToUpper)
                {
                    throw new ArgumentException("The push operation only supports GET and HEAD methods.", nameof(method));
                }

                int queryIndex = path.IndexOf('?');
                if (queryIndex >= 0)
                {
                    if (queryIndex < path.Length - 1)
                    {
                        query = path.Substring(queryIndex + 1);
                    }
                    path = path.Substring(0, queryIndex);
                }

                List<GCHandle> pinnedHeaders = null;
                if ((headers != null) && (headers.Count > 0))
                {
                    HttpApi.HTTP_REQUEST_HEADERS nativeHeaders = new HttpApi.HTTP_REQUEST_HEADERS();
                    pinnedHeaders = SerializeHeaders(headers, ref nativeHeaders);
                    nativeHeadersPointer = &nativeHeaders;
                }

                try
                {
                    statusCode = HttpApi.HttpDeclarePush(RequestContext.Server.RequestQueue.Handle, RequestContext.Request.RequestId, verb, path, query, nativeHeadersPointer);
                }
                finally
                {
                    if (pinnedHeaders != null)
                    {
                        FreePinnedHeaders(pinnedHeaders);
                    }
                }

                if (statusCode != ErrorCodes.ERROR_SUCCESS)
                {
                    throw new HttpSysException((int)statusCode);
                }
            }
        }

        private unsafe List<GCHandle> SerializeHeaders(IDictionary<string, StringValues> headers, ref HttpApi.HTTP_REQUEST_HEADERS nativeHeaders)
        {
            List<GCHandle> pinnedHeaders = null;

            if (headers.Count > 0)
            {
                int unknownHeadersCount = 0;
                HttpApi.HTTP_UNKNOWN_HEADER[] unknownHeaders = null;

                pinnedHeaders = new List<GCHandle>();

                foreach (var header in headers)
                {
                    if ((header.Value.Count > 0) && (HttpApi.HTTP_HEADER_ID.IndexOfKnownHeader(header.Key) == -1))
                    {
                        unknownHeadersCount += header.Value.Count;
                    }
                }

                try
                {
                    fixed (HttpApi.HTTP_KNOWN_HEADER* knownHeadersPointer = &nativeHeaders.KnownHeaders)
                    {
                        foreach (var header in headers)
                        {
                            if (header.Value.Count > 0)
                            {
                                int knownHeaderIndex = HttpApi.HTTP_HEADER_ID.IndexOfKnownHeader(header.Key);

                                if (knownHeaderIndex == -1)
                                {
                                    if (unknownHeaders == null)
                                    {
                                        unknownHeaders = new HttpApi.HTTP_UNKNOWN_HEADER[unknownHeadersCount];
                                        GCHandle unknownHeadersHandle = GCHandle.Alloc(unknownHeaders, GCHandleType.Pinned);
                                        pinnedHeaders.Add(unknownHeadersHandle);
                                        nativeHeaders.pUnknownHeaders = (HttpApi.HTTP_UNKNOWN_HEADER*)unknownHeadersHandle.AddrOfPinnedObject();
                                    }

                                    for (int headerValueIndex = 0; headerValueIndex < header.Value.Count; headerValueIndex++)
                                    {
                                        byte[] headerNameBytes = HeaderEncoding.GetBytes(header.Key);
                                        unknownHeaders[nativeHeaders.UnknownHeaderCount].NameLength = (ushort)headerNameBytes.Length;
                                        GCHandle headerNameHandle = GCHandle.Alloc(headerNameBytes, GCHandleType.Pinned);
                                        pinnedHeaders.Add(headerNameHandle);
                                        unknownHeaders[nativeHeaders.UnknownHeaderCount].pName = (sbyte*)headerNameHandle.AddrOfPinnedObject();

                                        byte[] headerValueBytes = HeaderEncoding.GetBytes(header.Value[headerValueIndex] ?? String.Empty);
                                        unknownHeaders[nativeHeaders.UnknownHeaderCount].RawValueLength = (ushort)headerValueBytes.Length;
                                        GCHandle headerValueHandle = GCHandle.Alloc(headerValueBytes, GCHandleType.Pinned);
                                        pinnedHeaders.Add(headerValueHandle);
                                        unknownHeaders[nativeHeaders.UnknownHeaderCount].pRawValue = (sbyte*)headerValueHandle.AddrOfPinnedObject();
                                        nativeHeaders.UnknownHeaderCount++;
                                    }
                                }
                                else
                                {
                                    byte[] headerValueBytes = HeaderEncoding.GetBytes(header.Value.ToString());
                                    knownHeadersPointer[knownHeaderIndex].RawValueLength = (ushort)headerValueBytes.Length;
                                    GCHandle headerValueHandle = GCHandle.Alloc(headerValueBytes, GCHandleType.Pinned);
                                    pinnedHeaders.Add(headerValueHandle);
                                    knownHeadersPointer[knownHeaderIndex].pRawValue = (sbyte*)headerValueHandle.AddrOfPinnedObject();
                                }
                            }
                        }
                    }
                }
                catch
                {
                    FreePinnedHeaders(pinnedHeaders);
                    throw;
                }
            }

            return pinnedHeaders;
        }
    }
}
