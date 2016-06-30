using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace ProxyServer
{
    class Program
    {

        static void Main()
        {
            if(ProxyServer.Server.StartProxyServer())
            {
                Console.WriteLine(String.Format("Server started on https://localhost, Press enter key to end"));
                Console.ReadLine();
            }

            Console.WriteLine("Press enter key to exit");
            Console.ReadLine();
        }
    }
    
}
