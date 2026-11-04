
namespace TradingHub.Indicators.Kvs
{
    /// <summary>
    /// Una serie de números por vela (entradas, salidas y variables de un script KVS). Lo que no se calculó es NaN.
    /// Escribir la misma vela otra vez la reemplaza (la vela en formación se recalcula con cada operación).
    /// </summary>
    public sealed class KvsSeries
    {
        private double[] _v = new double[256];
        private int _count;

        public void Clear()
        {
            Array.Fill(_v, double.NaN, 0, Math.Min(_v.Length, Math.Max(_count, 1)));
            _count = 0;
        }

        public void Set(int bar, double value)
        {
            if (bar < 0) return;
            if (bar >= _v.Length)
            {
                int n = _v.Length;
                while (n <= bar) n *= 2;
                var nv = new double[n];
                Array.Fill(nv, double.NaN);
                Array.Copy(_v, nv, _v.Length);
                _v = nv;
            }

            if (bar >= _count)
            {
                for (int i = _count; i < bar; i++) _v[i] = double.NaN;
                _count = bar + 1;
            }

            _v[bar] = value;
        }

        /// <summary>Valor de hace <paramref name="offset"/> velas (0 = esta); NaN si no hay.</summary>
        public double Get(int bar, int offset = 0)
        {
            int i = bar - offset;
            return i >= 0 && i < _count ? _v[i] : double.NaN;
        }

        public KvsSeries() => Array.Fill(_v, double.NaN);
    }

    /// <summary>
    /// Las funciones de los scripts KVS (ema, sma, stdev, highest, crossover…). Cada llamada del script tiene su propio
    /// lugar («sitio») donde guarda lo que necesita de las velas anteriores, así una misma función puede usarse varias veces.
    /// Todo es por número de vela: recalcular la vela actual no avanza nada dos veces.
    /// </summary>
    public sealed class KvsRuntime
    {
        private readonly KvsSeries[] _in;
        private readonly KvsSeries[] _out;
        private readonly KvsSeries[] _aux;
        private readonly HashSet<string> _alerted = new();

        public KvsRuntime(int sites)
        {
            _in = new KvsSeries[Math.Max(1, sites)];
            _out = new KvsSeries[_in.Length];
            _aux = new KvsSeries[_in.Length];
            for (int i = 0; i < _in.Length; i++)
            {
                _in[i] = new KvsSeries();
                _out[i] = new KvsSeries();
                _aux[i] = new KvsSeries();
            }
        }

        public void Reset()
        {
            foreach (var s in _in) s.Clear();
            foreach (var s in _out) s.Clear();
            foreach (var s in _aux) s.Clear();
            _alerted.Clear();
        }

        private static int Len(double n) => double.IsNaN(n) ? 1 : Math.Max(1, (int)Math.Round(n));

        public double Ema(int site, int bar, double v, double length) => Smooth(site, bar, v, 2.0 / (Len(length) + 1));

        public double Rma(int site, int bar, double v, double length) => Smooth(site, bar, v, 1.0 / Len(length));

        private double Smooth(int site, int bar, double v, double alpha)
        {
            double prev = _out[site].Get(bar, 1);
            double r = double.IsNaN(v) ? prev : double.IsNaN(prev) ? v : alpha * v + (1 - alpha) * prev;
            _out[site].Set(bar, r);
            return r;
        }

        /// <summary>Los últimos n valores (sin los NaN), del más viejo al más nuevo.</summary>
        private IEnumerable<double> Window(int site, int bar, double v, double length)
        {
            _in[site].Set(bar, v);
            int n = Len(length);
            for (int i = n - 1; i >= 0; i--)
            {
                double x = _in[site].Get(bar, i);
                if (!double.IsNaN(x)) yield return x;
            }
        }

        public double Sma(int site, int bar, double v, double length)
        {
            double sum = 0;
            int c = 0;
            foreach (var x in Window(site, bar, v, length))
            {
                sum += x;
                c++;
            }

            return c > 0 ? sum / c : double.NaN;
        }

        public double Wma(int site, int bar, double v, double length)
        {
            double sum = 0, w = 0;
            int k = 1;
            foreach (var x in Window(site, bar, v, length))
            {
                sum += x * k;
                w += k;
                k++;
            }

            return w > 0 ? sum / w : double.NaN;
        }

        public double Stdev(int site, int bar, double v, double length)
        {
            var xs = Window(site, bar, v, length).ToList();
            if (xs.Count == 0) return double.NaN;
            double m = xs.Average();
            return Math.Sqrt(xs.Sum(x => (x - m) * (x - m)) / xs.Count);
        }

        public double Highest(int site, int bar, double v, double length)
        {
            double r = double.NaN;
            foreach (var x in Window(site, bar, v, length)) r = double.IsNaN(r) ? x : Math.Max(r, x);
            return r;
        }

        public double Lowest(int site, int bar, double v, double length)
        {
            double r = double.NaN;
            foreach (var x in Window(site, bar, v, length)) r = double.IsNaN(r) ? x : Math.Min(r, x);
            return r;
        }

        public double Sum(int site, int bar, double v, double length)
        {
            double s = 0;
            foreach (var x in Window(site, bar, v, length)) s += x;
            return s;
        }

        public double Change(int site, int bar, double v, double length = 1)
        {
            _in[site].Set(bar, v);
            return v - _in[site].Get(bar, Len(length));
        }

        /// <summary>Suma acumulada desde la primera vela.</summary>
        public double Cum(int site, int bar, double v)
        {
            double prev = _out[site].Get(bar, 1);
            double r = (double.IsNaN(prev) ? 0 : prev) + (double.IsNaN(v) ? 0 : v);
            _out[site].Set(bar, r);
            return r;
        }

        public bool Crossover(int site, int bar, double a, double b)
        {
            _in[site].Set(bar, a);
            _aux[site].Set(bar, b);
            double pa = _in[site].Get(bar, 1), pb = _aux[site].Get(bar, 1);
            return a > b && pa <= pb;
        }

        public bool Crossunder(int site, int bar, double a, double b)
        {
            _in[site].Set(bar, a);
            _aux[site].Set(bar, b);
            double pa = _in[site].Get(bar, 1), pb = _aux[site].Get(bar, 1);
            return a < b && pa >= pb;
        }

        public static double Nz(double v, double d = 0) => double.IsNaN(v) || double.IsInfinity(v) ? d : v;

        public static bool Na(double v) => double.IsNaN(v);

        public static decimal D(double v) => double.IsNaN(v) || double.IsInfinity(v) ? 0 : (decimal)Math.Clamp(v, -7.9e27, 7.9e27);

        /// <summary>Alerta del script: una por vela (en la última) y por texto.</summary>
        public void Alert(Indicator owner, int bar, string message, bool lastBar)
        {
            if (!lastBar || !_alerted.Add(bar + "|" + message)) return;
            owner.AddAlertFromScript(message);
        }

        /// <summary>«#RRGGBB» o «#AARRGGBB» (vacío = <paramref name="fallback"/>).</summary>
        public static System.Drawing.Color ParseColor(string? hex, System.Drawing.Color fallback)
        {
            if (string.IsNullOrWhiteSpace(hex)) return fallback;
            var h = hex.Trim().TrimStart('#');
            try
            {
                if (h.Length == 6) return System.Drawing.Color.FromArgb(255, Convert.ToInt32(h[..2], 16), Convert.ToInt32(h[2..4], 16), Convert.ToInt32(h[4..], 16));
                if (h.Length == 8) return System.Drawing.Color.FromArgb(Convert.ToInt32(h[..2], 16), Convert.ToInt32(h[2..4], 16), Convert.ToInt32(h[4..6], 16), Convert.ToInt32(h[6..], 16));
            }
            catch
            {
                // color inválido
            }

            return fallback;
        }

        /// <summary>Liquidez del libro cerca del precio en la vela (promedio de las fotos; NaN sin fotos).</summary>
        public static (double Bid, double Ask) Book(IReadOnlyList<TradingHub.Data.DepthSample> samples, int withinTicks)
        {
            if (samples.Count == 0) return (double.NaN, double.NaN);
            double b = 0, a = 0;
            foreach (var s in samples)
            {
                var (x, y) = s.Within(withinTicks);
                b += x;
                a += y;
            }

            return (b / samples.Count, a / samples.Count);
        }
    }
}
