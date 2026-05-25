using UnityEngine;

namespace PuckMetrics
{
    public class MetricsMod : IPuckPlugin
    {
        private GameObject _driverObject;

        public bool OnEnable()
        {
            Debug.Log("[PuckMetrics] Enabled.");

            _driverObject = new GameObject("PuckMetrics_Driver");
            _driverObject.AddComponent<MetricsDriver>();
            Object.DontDestroyOnLoad(_driverObject);

            return true;
        }

        public bool OnDisable()
        {
            Debug.Log("[PuckMetrics] Disabled.");

            if (_driverObject == null)
                return true;

            Object.Destroy(_driverObject);
            _driverObject = null;

            return true;
        }
    }
}
