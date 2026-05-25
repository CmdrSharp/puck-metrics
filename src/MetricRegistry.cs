using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PuckMetrics
{
    public enum MetricType
    {
        Gauge,
        Counter
    }

    public class MetricFamily
    {
        public string Name { get; }
        public string Help { get; }
        public MetricType Type { get; }

        private readonly Dictionary<string, MetricSample> _samples = new Dictionary<string, MetricSample>();

        public MetricFamily(string name, string help, MetricType type)
        {
            Name = name;
            Help = help;
            Type = type;
        }

        public void Set(double value, params string[] labelValues)
        {
            var key = string.Join("\x1f", labelValues);

            if (!_samples.TryGetValue(key, out var sample))
            {
                sample = new MetricSample(labelValues);
                _samples[key] = sample;
            }

            sample.Value = value;
        }

        public void Inc(double amount, params string[] labelValues)
        {
            var key = string.Join("\x1f", labelValues);

            if (!_samples.TryGetValue(key, out var sample))
            {
                sample = new MetricSample(labelValues);
                _samples[key] = sample;
            }

            sample.Value += amount;
        }

        public void Reset()
        {
            _samples.Clear();
        }

        public IReadOnlyDictionary<string, MetricSample> Samples => _samples;
    }

    public class MetricSample
    {
        public string[] LabelValues { get; }
        public double Value { get; set; }

        public MetricSample(string[] labelValues)
        {
            LabelValues = labelValues;
        }
    }

    public class MetricRegistry
    {
        private readonly List<(MetricFamily family, string[] labelNames)> _families
            = new List<(MetricFamily, string[])>();

        public MetricFamily CreateGauge(string name, string help, params string[] labelNames)
        {
            var family = new MetricFamily(name, help, MetricType.Gauge);
            _families.Add((family, labelNames));

            return family;
        }

        public MetricFamily CreateCounter(string name, string help, params string[] labelNames)
        {
            var family = new MetricFamily(name, help, MetricType.Counter);
            _families.Add((family, labelNames));

            return family;
        }

        public string Expose()
        {
            var sb = new StringBuilder(4096);

            foreach (var (family, labelNames) in _families)
            {
                sb.Append("# HELP ").Append(family.Name).Append(' ').AppendLine(family.Help);
                sb.Append("# TYPE ").Append(family.Name).Append(' ')
                    .AppendLine(family.Type == MetricType.Gauge ? "gauge" : "counter");

                foreach (var kvp in family.Samples)
                {
                    var sample = kvp.Value;
                    sb.Append(family.Name);

                    if (labelNames.Length > 0 && sample.LabelValues.Length > 0)
                    {
                        sb.Append('{');

                        for (int i = 0; i < labelNames.Length && i < sample.LabelValues.Length; i++)
                        {
                            if (i > 0) sb.Append(',');
                            sb.Append(labelNames[i]).Append("=\"")
                                .Append(EscapeLabelValue(sample.LabelValues[i]))
                                .Append('"');
                        }

                        sb.Append('}');
                    }

                    sb.Append(' ').AppendLine(sample.Value.ToString(CultureInfo.InvariantCulture));
                }
            }

            return sb.ToString();
        }

        private static string EscapeLabelValue(string value)
        {
            if (value == null) return "";
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
        }
    }
}
