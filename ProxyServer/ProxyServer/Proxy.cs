using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ProxyServer
{
    public class Proxy
    {
        private TcpListener m_Listener;
        private static Proxy s_Proxy = new Proxy();
        private readonly Regex cookieSplitRegEx = new Regex(@",(?! )");

        public IPAddress ListeningIP
        {
            get
            {
                IPAddress addr = IPAddress.Loopback;
                if (ConfigurationManager.AppSettings["ListeningIP"] != null)
                    IPAddress.TryParse(ConfigurationManager.AppSettings["ListeningIP"], out addr);

                return addr;
            }
        }

        public Int32 ListeningPort
        {
            get
            {
                Int32 port = 82;
                if (ConfigurationManager.AppSettings["ListeningPort"] != null)
                    Int32.TryParse(ConfigurationManager.AppSettings["ListeningPort"], out port);

                return port;
            }
        }

        private Proxy()
        {
            this.m_Listener = new TcpListener(this.ListeningIP, this.ListeningPort);
        }

        public static Proxy GetInstance()
        {
            return s_Proxy;
        }

        public bool Start()
        {
            try
            {
                this.m_Listener.Start();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return false;
            }

            Thread listenThread = new Thread(new ParameterizedThreadStart(Listen));

            listenThread.Start(this.m_Listener);

            return true;
        }

        private void Listen(Object obj)
        {
            TcpListener listener = (TcpListener)obj;
            try
            {
                while (true)
                {
                    TcpClient client = listener.AcceptTcpClient();
                    while (!ThreadPool.QueueUserWorkItem(new WaitCallback(sendClientForCallback), client)) ;
                }
            }
            catch (ThreadAbortException) { }
            catch (SocketException) { }
        }

        private void sendClientForCallback(object client)
        {
            DoHttpProcessing(client as TcpClient);
        }

        private void DoHttpProcessing(TcpClient client)
        {
            try
            {

            }
            catch { }
        }

        private List<Tuple<String, String>> ProcessResponse(HttpWebResponse response)
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
                else
                    returnHeaders.Add(new Tuple<String, String>(s, response.Headers[s]));
            }

            if (!String.IsNullOrWhiteSpace(value))
            {
                response.Headers.Remove(header);
                String[] cookies = cookieSplitRegEx.Split(value);
                foreach (String cookie in cookies)
                    returnHeaders.Add(new Tuple<String, String>("Set-Cookie", cookie));

            }
            returnHeaders.Add(new Tuple<String, String>("X-Proxied-By", "matt-dot-net proxy"));
            return returnHeaders;
        }

        private  void WriteResponseHeaders(StreamWriter myResponseWriter, List<Tuple<String, String>> headers)
        {
            if (headers != null)
            {
                foreach (Tuple<String, String> header in headers)
                    myResponseWriter.WriteLine(String.Format("{0}: {1}", header.Item1, header.Item2));
            }
            myResponseWriter.WriteLine();
            myResponseWriter.Flush();
        }

        private int ReadRequestHeaders(StreamReader sr, HttpWebRequest webReq)
        {
            String httpCmd;
            int contentLen = 0;
            do
            {
                httpCmd = sr.ReadLine();
                if (String.IsNullOrEmpty(httpCmd))
                    return contentLen;
                String[] header = httpCmd.Split(new string[] { ": " }, 2, StringSplitOptions.None);
                switch (header[0].ToLower())
                {
                    case "host":
                        //webReq.Host = "172.23.32.43";
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
                        String[] sb = header[1].Trim().Split(new char[] { ';' });
                        DateTime d;
                        if (DateTime.TryParse(sb[0], out d))
                            webReq.IfModifiedSince = d;
                        break;
                    default:
                        try
                        {
                            //webReq.Headers.Add(header[0], header[1]);
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

        private void WriteResponseStatus(HttpStatusCode code, String description, StreamWriter myResponseWriter)
        {
            String s = String.Format("HTTP/1.0 {0} {1}", (Int32)code, description);
            myResponseWriter.WriteLine(s);
        }
    }
}
