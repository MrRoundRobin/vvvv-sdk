using System;
using System.ComponentModel.Composition;

using VVVV.PluginInterfaces.V2;

namespace VVVV.Nodes
{
    [PluginInfo(Name = "Sync", Category = "Network", Version = "IO", Tags = "OSC, remote", Author = "Robster", Help = "Synchronizes IO boxes via OSC", AutoEvaluate = true)]
    public class SyncNetworkIO : IPluginEvaluate, IDisposable
    {
#pragma warning disable 0649

        [Input("Local Port", DefaultValue = 44444, IsSingle = true, AsInt = true)]
        public IDiffSpread<int> LocalPortIn;
            
        [Input("Remote Host", DefaultString = "10.0.0.5", IsSingle = true, StringType = StringType.IP)]
        public IDiffSpread<string> RemoteHostIn;

        [Input("Remote Port", DefaultValue = 55555, IsSingle = true, AsInt = true)]
        public IDiffSpread<int> RemotePortIn;
            
        [Input("Recursive", IsSingle = true)]
        public IDiffSpread<bool> RecursiveIn;

#pragma warning restore

        [Import]
        internal static IHDEHost HDEHost;

        private static IOManager _manager;
        private readonly string _path;
        private bool _disposed;

        private bool _registered;    

        [ImportingConstructor]
        public SyncNetworkIO(IHDEHost host, IPluginHost2 pluginHost)
        {
            if (HDEHost == null)
                HDEHost = host;

            _path = pluginHost.ParentNode.GetNodePath(false);

            _manager = IOManager.Instance;
        }

        ~SyncNetworkIO()
        {
            Dispose(false);
        }

        public void Evaluate(int SpreadMax)
        {
            if (!_registered)
            {
                _manager.AddPatch(_path, LocalPortIn[0], RemoteHostIn[9], RemotePortIn[0], RecursiveIn[0]);
                _registered = true;
            }

            if (LocalPortIn.IsChanged || RemoteHostIn.IsChanged || RemotePortIn.IsChanged || RecursiveIn.IsChanged)
                _manager.Update(_path, LocalPortIn[0], RemoteHostIn[9], RemotePortIn[0], RecursiveIn[0]);

            _manager.ProcessQueue();
        }

        public void Dispose()
        {
            Dispose(true);
        }

        protected void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _manager.RemovePatch(_path);
                }
            }
        }

    }
}
