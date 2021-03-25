/*
    Copyright 2013 Phillip Schichtel

    This file is part of LyricsReloaded.

    LyricsReloaded is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    LyricsReloaded is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with LyricsReloaded. If not, see <http://www.gnu.org/licenses/>.

*/

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using System.Net;
using System.IO;
using System.IO.Compression;
using System.Reflection;

namespace CubeIsland.LyricsReloaded
{
    public class WebClient
    {
        private static readonly Regex ENCODING_REGEX = new Regex("<meta\\s+http-equiv=[\"']?content-type[\"']?\\s+content=.*?;\\s*charset\\s*=\\s*([a-z0-9-]+)[^>]*>|<\\?xml.+?encoding=\"([^\"]).*?\\?>|<meta\\s*data-react-helmet=\"true\"\\s*charset=\"([^\"]*?)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly LyricsReloaded lyricsReloaded;
        private readonly int timeout;

        public WebClient(LyricsReloaded lyricsReloaded, int timeout)
        {
            this.lyricsReloaded = lyricsReloaded;
            this.timeout = timeout;
        }

        protected HttpWebRequest newRequest(string method, string url, IDictionary<String, String> headers)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = method;
            request.Timeout = timeout;
            request.Proxy = lyricsReloaded.getProxy();
            if (headers != null)
            {
                foreach (KeyValuePair<String, String> header in headers)
                {
                    this.setHeader(request, header.Key, header.Value);
                }
            }

            return request;
        }

        protected String headerToProperty(String header)
        {
            StringBuilder property = new StringBuilder();

            bool nextUpper = true;
            foreach (char c in header)
            {
                if (c == '-')
                {
                    nextUpper = true;
                    continue;
                }
                if (nextUpper)
                {
                    property.Append(Char.ToUpper(c));
                    nextUpper = false;
                }
                else
                {
                    property.Append(Char.ToLower(c));
                }
            }

            return property.ToString();
        }

        protected void setHeader(HttpWebRequest request, String name, String value)
        {
            name = name.ToLower();
            String prop = headerToProperty(name);
            PropertyInfo propInfo = request.GetType().GetProperty(prop);
            
            if (propInfo.CanWrite)
            {
                if (propInfo.PropertyType == typeof(String))
                {
                    propInfo.SetValue(request, value, null);
                    return;
                }
                else if (propInfo.PropertyType == typeof(DateTime))
                {
                    propInfo.SetValue(request, DateTime.Parse(value), null);
                    return;
                }
            }

            if (name.Equals("range"))
            {
                //    request.AddRange()
                //    break;
                return;
            }

            request.Headers.Set(name, value);
        }

        public int getTimeout()
        {
            return timeout;
        }

        public WebResponse get(string url, IDictionary<String, String> headers = null)
        {
            return executeRequest(newRequest("GET", url, headers));
        }

        public WebResponse post(string url, Dictionary<string, string> data, IDictionary<String, String> headers = null)
        {
            HttpWebRequest request = newRequest("POST", url, headers);

            request.ContentType = "application/x-www-form-urlencoded";
            request.ContentLength = 0;

            if (data != null)
            {
                Stream stream;
                try
                {
                    stream = request.GetRequestStream();
                }
                catch (WebException e)
                {
                    if (e.Status == WebExceptionStatus.Timeout)
                    {
                        throw new WebException("The operation has timed out.");
                    }
                    throw;
                }
                using (stream)
                {
                    stream.WriteTimeout = timeout;
                    string queryString = generateQueryString(data);
                    byte[] byteData = new byte[Encoding.UTF8.GetByteCount(queryString)];
                    Encoding.UTF8.GetBytes(queryString, 0, queryString.Length, byteData, 0);
                    stream.Write(byteData, 0, byteData.Length);
                    request.ContentLength += byteData.Length;
                }
            }

            return executeRequest(request);
        }

        public static string generateQueryString(IEnumerable<KeyValuePair<string, string>> data)
        {
            StringBuilder queryString = new StringBuilder("");
            IEnumerator<KeyValuePair<string, string>> it = data.GetEnumerator();

            if (it.MoveNext())
            {
                queryString.Append(HttpUtility.UrlEncode(it.Current.Key, Encoding.UTF8))
                           .Append('=')
                           .Append(HttpUtility.UrlEncode(it.Current.Value, Encoding.UTF8));

                while (it.MoveNext())
                {
                    queryString.Append('&').Append(HttpUtility.UrlEncode(it.Current.Key, Encoding.UTF8))
                               .Append('=').Append(HttpUtility.UrlEncode(it.Current.Value, Encoding.UTF8));
                }
            }


            return queryString.ToString();
        }

        protected WebResponse executeRequest(HttpWebRequest request)
        {
            // request configration
            string[] acceptEncodingValues = request.Headers.GetValues("Accept-Encoding");
            if (acceptEncodingValues == null || acceptEncodingValues.Length == 0)
            {
                // we support gzip if nothing else was specified.
                request.Headers.Add("Accept-Encoding", "gzip");
            }
            if (request.UserAgent == null)
            {
                request.UserAgent = lyricsReloaded.getDefaultUserAgent();
            }
            
            request.Accept = "*/*";
            request.Headers.Add("Accept-Encoding", "gzip");

            // load the response
            WebResponse webResponse;
            String contentString = null;
            Encoding encoding = Encoding.ASCII; // default encoding as the last fallback
            HttpWebResponse response;
            try
            {
                response = (HttpWebResponse) request.GetResponse();
            }
            catch (WebException e)
            {
                if (e.Status == WebExceptionStatus.Timeout)
                {
                    throw new WebException("The operation has timed out.", e);
                }
                else if (e.Status == WebExceptionStatus.ProtocolError || e.Status == WebExceptionStatus.UnknownError)
                {
                    throw new WebException("Ops, something went wrong. (Report this please)", e);
                }
                throw;
            }
            using (response)
            {
                if (response.CharacterSet != null)
                {
                    encoding = Encoding.GetEncoding(response.CharacterSet); // the response encoding specified by the server. this should be enough
                }

                Stream responsesStream = response.GetResponseStream();
                if (responsesStream != null)
                {
                    responsesStream.ReadTimeout = timeout;
                    if (String.Compare(response.ContentEncoding, "gzip", StringComparison.OrdinalIgnoreCase) == 0)
                    {
                        // gzip compression detected, wrap the stream with a decompressing gzip stream
                        lyricsReloaded.getLogger().debug("gzip compression detected");
                        responsesStream = new GZipStream(responsesStream, CompressionMode.Decompress);
                    }
                    MemoryStream content = new MemoryStream();
                    const int bufferSize = 4096;
                    byte[] buffer = new byte[bufferSize];
                    int bytesRead;

                    do
                    {
                        bytesRead = responsesStream.Read(buffer, 0, bufferSize);
                        if (bytesRead <= 0)
                        {
                            break;
                        }
                        content.Write(buffer, 0, bytesRead);
                    }
                    while (bytesRead > 0);
                    responsesStream.Close();

                    contentString = encoding.GetString(content.GetBuffer()); // decode the data with the currently known encoding
                    Match match = ENCODING_REGEX.Match(contentString); // search for a encoding specified in the content
                    if (match.Success)
                    {
                        try
                        {
                            Encoding tmp = Encoding.GetEncoding(match.Groups[1].ToString()); // try to get a encoding from the name
                            if (!encoding.Equals(tmp))
                            {
                                encoding = tmp;
                                contentString = encoding.GetString(content.GetBuffer()); // decode again with the newly found encoding
                            }
                        }
                        catch (ArgumentException)
                        {}
                    }
                    content.Close();
                }
                webResponse = new WebResponse(contentString, encoding, response.Headers);
            }

            return webResponse;
        }
    }

    public class WebResponse
    {
        private readonly string content;
        private readonly Encoding encoding;
        private readonly WebHeaderCollection headers;

        public WebResponse(string content, Encoding encoding, WebHeaderCollection headers)
        {
            this.content = content;
            this.encoding = encoding;
            this.headers = headers;
        }

        public string getContent()
        {
            return content;
        }

        public Encoding getEncoding()
        {
            return encoding;
        }

        public WebHeaderCollection getHeaders()
        {
            return headers;
        }
    }
}
