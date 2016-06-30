using HtmlAgilityPack;
using ProxyServer.Properties;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace ProxyServer
{
    public class ProxyServer
    {
        private TcpListener m_Listener;
        private Thread m_ListenerThread;
        private static readonly int BUFFER_SIZE = 8192;
        private static readonly char[] s_SemiSplit = new char[] { ';' };
        private static readonly char[] s_EqualSplit = new char[] { '=' };
        private static readonly String[] s_ColonSpaceSplit = new string[] { ": " };
        private static readonly char[] s_SpaceSplit = new char[] { ' ' };
        private static readonly char[] s_CcommaSplit = new char[] { ',' };
        private static X509Certificate2 s_SelfCertificate;
        private static IPAddress s_ListeningIPAddress = IPAddress.Parse("127.0.0.1");
        private static int s_Port = 443; //ssl
        private static readonly ProxyServer s_ProxyServer = new ProxyServer();
        private static string s_RootUrl = @"https://localhost/?url=";
        private static string s_SiteRootUrl = "";


        public static ProxyServer Server
        {
            get { return s_ProxyServer; }
        }

        private ProxyServer()
        {
            this.m_Listener = new TcpListener(s_ListeningIPAddress, s_Port);
            ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };
        }

        public bool StartProxyServer()
        {
            bool isStart = true;
            try
            {
                try
                {
                    s_SelfCertificate = new X509Certificate2(Resources.serverpfx, "Ab123456");
                }
                catch (Exception ex)
                {
                    throw new ConfigurationErrorsException(String.Format("Could not create the certificate"), ex);
                }

                this.m_Listener.Start();
                this.m_ListenerThread = new Thread(new ParameterizedThreadStart(Listen));
                this.m_ListenerThread.Start(m_Listener);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                isStart = false;
            }

            return isStart;
        }

        public void StopProxyServer()
        {
            this.m_Listener.Stop();
            this.m_ListenerThread.Abort();
            this.m_ListenerThread.Join();
            this.m_ListenerThread.Join();
        }

        private void Listen(Object i_Listener)
        {
            TcpListener listener = i_Listener as TcpListener;

            try
            {
                while (true)
                {
                    TcpClient client = listener.AcceptTcpClient();
                    while (!ThreadPool.QueueUserWorkItem(new WaitCallback(ProxyServer.ProcessClient), client)) ;
                }
            }
            catch (ThreadAbortException) { }
            catch (SocketException) { }
        }

        private static void ProcessClient(Object i_Listener)
        {
            TcpClient client = i_Listener as TcpClient;
            try
            {
                DoHttpProcessing(client);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
            finally
            {
                client.Close();
            }
        }

        private static void DoHttpProcessing(TcpClient i_Client)
        {

            var sslStream = new SslStream(i_Client.GetStream(), true);
            bool isHtml = false;

            try
            {
                sslStream.AuthenticateAsServer(s_SelfCertificate, false, SslProtocols.Tls | SslProtocols.Ssl3 | SslProtocols.Ssl2, true);
            }
            catch (Exception)
            {
                sslStream.Close();
                return;
            }

            var clientStream = sslStream;
            var clientStreamReader = new StreamReader(sslStream);
            var outStream = sslStream;
            var httpCmd = clientStreamReader.ReadLine();


            if (String.IsNullOrEmpty(httpCmd))
            {
                clientStreamReader.Close();
                clientStream.Close();
                sslStream.Close();
            }
            else
            {

                var splitBuffer = httpCmd.Split(s_SpaceSplit, 3);
                string url = "";
                if (splitBuffer[1].Contains(@"/?url=") && string.IsNullOrEmpty(s_SiteRootUrl))
                {
                    s_SiteRootUrl = splitBuffer[1].Replace(@"/?url=", "");
                    url = s_SiteRootUrl;
                }
                else if (splitBuffer[1].EndsWith(@".html"))
                {
                    url = splitBuffer[1].Replace("/?url=", "");
                    if (!url.ToLower().Contains("http"))
                    {
                        url = s_SiteRootUrl + url;
                    }

                    isHtml = true;
                }
                else if(!splitBuffer[1].ToLower().Contains(s_RootUrl))
                {
                    url = string.Format(@"{0}{1}", s_SiteRootUrl, splitBuffer[1]);
                }

                string method = splitBuffer[0];
                Version version = new Version(1, 0);
                HttpWebRequest webReq;
                HttpWebResponse response = null;

                webReq = (HttpWebRequest)HttpWebRequest.Create(url);
                webReq.Method = method;
                webReq.ProtocolVersion = version;
                webReq.KeepAlive = false;
                webReq.AllowAutoRedirect = false;
                webReq.AutomaticDecompression = DecompressionMethods.None;
                int contentLen = ReadRequestHeaders(clientStreamReader, webReq);

                if (method.ToUpper() == "POST")
                {
                    char[] postBuffer = new char[contentLen];
                    int bytesRead;
                    int totalBytesRead = 0;
                    StreamWriter sw = new StreamWriter(webReq.GetRequestStream());
                    while (totalBytesRead < contentLen && (bytesRead = clientStreamReader.ReadBlock(postBuffer, 0, contentLen)) > 0)
                    {
                        totalBytesRead += bytesRead;
                        sw.Write(postBuffer, 0, bytesRead);
                    }

                    sw.Close();
                }

                try
                {
                    response = (HttpWebResponse)webReq.GetResponse();
                }
                catch (WebException webEx)
                {
                    response = webEx.Response as HttpWebResponse;
                }

                if (response != null)
                {
                    List<Tuple<String, String>> responseHeaders = i_ProcessResponse(response);
                    StreamWriter myResponseWriter = new StreamWriter(outStream);
                    Stream responseStream = response.GetResponseStream();
                    WriteResponseStatus(response.StatusCode, response.StatusDescription, myResponseWriter);
                    WriteResponseHeaders(myResponseWriter, responseHeaders);

                    if (isHtml)
                    {
                       // var bytes2 = handelHtml(url);

                        //var buffer2 = Encoding.UTF8.GetBytes(handelHtml(url));
                        //var stream = response.GetResponseStream();
                        //int bytesRead;
                        //while ((bytesRead = stream.Read(buffer2, 0, buffer2.Length)) > 0)
                        //{
                        //    //outStream.Write(buffer2, 0, bytesRead);
                        //}

                        //responseStream.Close();
                        //outStream.Flush();
                        isHtml = false;
                    }

                    try
                    {

                        Byte[] buffer;
                        if (response.ContentLength > 0)
                            buffer = new Byte[response.ContentLength];
                        else
                            buffer = new Byte[BUFFER_SIZE];

                        int bytesRead;

                        while ((bytesRead = responseStream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            outStream.Write(buffer, 0, bytesRead);
                        }

                        responseStream.Close();
                        outStream.Flush();

                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(ex.Message);
                    }
                    finally
                    {
                        responseStream.Close();
                        response.Close();
                        myResponseWriter.Close();
                    }

                }
            }
        }

        private static string handelHtml(string i_Url)
        {
            HtmlWeb hw = new HtmlWeb();
            HtmlAgilityPack.HtmlDocument doc = new HtmlAgilityPack.HtmlDocument();
            doc = hw.Load(i_Url);
            foreach (HtmlNode link in doc.DocumentNode.SelectNodes("//a[@href]"))
            {
                // Get the value of the HREF attribute
                string hrefValue = link.GetAttributeValue("href", string.Empty);
                if (hrefValue.ToLower().Contains("http"))
                {
                    //link.SetAttributeValue("href", s_RootUrl + hrefValue);
                }
            }

            StringWriter sw = new StringWriter();
            doc.DocumentNode.WriteTo(sw);
            sw.Flush();

            return sw.ToString();
        }

        private static void WriteResponseStatus(HttpStatusCode i_Code, String i_Description, StreamWriter i_MyResponseWriter)
        {
            String s = String.Format("HTTP/1.0 {0} {1}", (Int32)i_Code, i_Description);
            i_MyResponseWriter.WriteLine(s);
        }

        private static void WriteResponseHeaders(StreamWriter i_MyResponseWriter, List<Tuple<String, String>> i_Headers)
        {
            if (i_Headers != null)
            {
                foreach (Tuple<String, String> header in i_Headers)
                    i_MyResponseWriter.WriteLine(String.Format("{0}: {1}", header.Item1, header.Item2));
            }

            i_MyResponseWriter.WriteLine();
            i_MyResponseWriter.Flush();
        }

        private static int ReadRequestHeaders(StreamReader sr, HttpWebRequest webReq)
        {
            String httpCmd;
            int contentLen = 0;
            do
            {
                httpCmd = sr.ReadLine();
                if (String.IsNullOrEmpty(httpCmd))
                    return contentLen;
                String[] header = httpCmd.Split(s_ColonSpaceSplit, 2, StringSplitOptions.None);
                switch (header[0].ToLower())
                {
                    case "host":
                        //webReq.Host = s_RootUrl;
                        break;
                    case "user-agent":
                        webReq.UserAgent = header[1];
                        break;
                    case "accept":
                        webReq.Accept = header[1];
                        break;
                    case "referer":
                        webReq.Referer = header[1];
                        break;
                    case "cookie":
                        webReq.Headers["Cookie"] = header[1];
                        break;
                    case "proxy-connection":
                    case "connection":
                    case "keep-alive":
                        //ignore these
                        break;
                    case "content-length":
                        int.TryParse(header[1], out contentLen);
                        break;
                    case "content-type":
                        webReq.ContentType = header[1];
                        break;
                    case "if-modified-since":
                        String[] sb = header[1].Trim().Split(s_SemiSplit);
                        DateTime d;
                        if (DateTime.TryParse(sb[0], out d))
                            webReq.IfModifiedSince = d;
                        break;
                    default:
                        try
                        {
                            webReq.Headers.Add(header[0], header[1]);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine(String.Format("Could not add header {0}.  Exception message:{1}", header[0], ex.Message));
                        }
                        break;
                }
            } while (!String.IsNullOrWhiteSpace(httpCmd));

            return contentLen;
        }

        private static List<Tuple<String, String>> i_ProcessResponse(HttpWebResponse response, int i_Length = 0)
        {
            String value = null;
            String header = null;
            List<Tuple<String, String>> returnHeaders = new List<Tuple<String, String>>();

            foreach (String s in response.Headers.Keys)
            {
                if (s.ToLower() == "set-cookie")
                {
                    header = s;
                    value = response.Headers[s];
                }
                else if (s.ToLower() == "location")
                {
                    if (!response.Headers[s].ToLower().Contains(s_RootUrl))
                    {
                        returnHeaders.Add(new Tuple<String, String>(s, s_RootUrl + s_SiteRootUrl.Remove(s_SiteRootUrl.Length - 1, 0) + response.Headers[s]));
                    }
                    else
                    {
                        returnHeaders.Add(new Tuple<String, String>(s, response.Headers[s]));
                    }
                }
                else
                    returnHeaders.Add(new Tuple<String, String>(s, response.Headers[s]));
            }

            if (!String.IsNullOrWhiteSpace(value))
            {
                response.Headers.Remove(header);
            }

            return returnHeaders;
        }
    }
}
