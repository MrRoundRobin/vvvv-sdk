using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;
using VVVV.Core.Logging;
using VVVV.PluginInterfaces.V2;
using VVVV.PluginInterfaces.V2.Graph;
using VVVV.Utils.Concurrent;
using VVVV.Utils.OSC;

namespace VVVV.Nodes
{
    internal class IOManager
    {
        private static readonly Lazy<IOManager> lazy = new Lazy<IOManager>(() => new IOManager());

        private IHDEHost _HDEHost;

        private Dictionary<int, OSCReceiver> _receiver = new Dictionary<int, OSCReceiver>();
        private List<int> _oldPorts = new List<int>(); 

        private Dictionary<string, Config> _instanceConfig = new Dictionary<string, Config>(); 

        private Dictionary<IPin2, INode2>  _exposedNodes = new Dictionary<IPin2, INode2>();

        private Dictionary<int, Thread> _listeningThreads = new Dictionary<int, Thread>();

        private BlockingCollection<ChangeMessage> _queue = new BlockingCollection<ChangeMessage>(); 

        private struct ChangeMessage
        {
            public int Port;
            public string Name;
            public ArrayList Value;
        }

        private struct Config
        {
            public int LocalPort;
            public int RemotePort;
            public string RemoteHost;
            public bool Recursive;
        }

        public static IOManager Instance
        {
            get
            {
                return lazy.Value;
            }
        }

        private IOManager()
        {
            _HDEHost = SyncNetworkIO.HDEHost;

            _HDEHost.ExposedNodeService.Nodes.ToList().ForEach(ExposedNodeAdded);

            _HDEHost.ExposedNodeService.NodeAdded += ExposedNodeAdded;
            _HDEHost.ExposedNodeService.NodeRemoved += ExposedNodeRemoved;

        }

        private void ExposedNodeAdded(INode2 node)
        {
            var pin = node.FindPin(PinNameFromNode(node));

            _exposedNodes.Add(pin, node);

            node.FindPin(PinNameFromNode(node)).Changed += PinChanged;
        }

        private void ExposedNodeRemoved(INode2 node)
        {
            try
            {
                _exposedNodes.Remove(node.FindPin(PinNameFromNode(node)));
                node.FindPin(PinNameFromNode(node)).Changed -= PinChanged;
            }
            catch (ArgumentNullException)
            {
                System.Diagnostics.Debug.WriteLine("ahhhhhh sub patch gelöscht?!");
            }

        }

        private void PinChanged(object sender, EventArgs eventArgs)
        {
            var pin = sender as IPin2;

            if (pin == null) return;

            var node = _exposedNodes[pin];

            var path = node.Parent.GetNodePath(false);

            string listPath = null;

            if (_instanceConfig.ContainsKey(path))
                listPath = path;
            else if (_instanceConfig.Any(c => c.Value.Recursive && path.StartsWith(c.Key)))
                listPath = _instanceConfig.First(c => c.Value.Recursive && path.StartsWith(c.Key)).Key;

            // TODO: .All()

            if (listPath == null || !_instanceConfig.ContainsKey(path)) return;

            var config = _instanceConfig[path];

            var bundle = new OSCBundle();
            var message = new OSCMessage("/"+ node.LabelPin.Spread);

            var values = new ArrayList();

            for (int i = 0; i < pin.SliceCount; i++)
            {
                message.Append(pin[i]);
                values.Add(pin[i]);
            }

            _queue.Add(new ChangeMessage()
            {
                Port = config.LocalPort,
                Name = node.LabelPin.Spread,
                Value = values
            });

            bundle.Append(message);

            try
            {
                var osc = new OSCTransmitter(config.RemoteHost, config.RemotePort);
                osc.Connect();
                osc.Send(bundle);
                osc.Close();
            }
            catch (Exception e)
            {
                System.Diagnostics.Debugger.Break();    
            }
        }

        private string PinNameFromNode(INode2 node)
        {
            string pinName = "";
            if (node.NodeInfo.Systemname == "IOBox (Value Advanced)")
                pinName = "Y Input Value";
            else if (node.NodeInfo.Systemname == "IOBox (String)")
                pinName = "Input String";
            else if (node.NodeInfo.Systemname == "IOBox (Color)")
                pinName = "Color Input";
            else if (node.NodeInfo.Systemname == "IOBox (Enumerations)")
                pinName = "Input Enum";
            else if (node.NodeInfo.Systemname == "IOBox (Node)")
                pinName = "Input Node";

            return pinName;
        }

        private void StartListening(int port)
        {
            if (_listeningThreads.ContainsKey(port) && _listeningThreads[port].IsAlive) return;

            if (_listeningThreads.ContainsKey(port))
                _listeningThreads.Remove(port);

            _listeningThreads.Add(port, new Thread(() => Listen(port)));
            _listeningThreads[port].Start();
        }

        private void StopListening(int port)
        {
            if (!_listeningThreads.ContainsKey(port)) return;

            _receiver[port].Close();
            _receiver.Remove(port);
            _listeningThreads[port].Abort();
            //_listeningThreads.Remove(port);
            //TODO: Geht das? sollte!
        }

        public void AddPatch(string path, int localPort, string remoteHost, int remotePort, bool recursive = false)
        {
            _instanceConfig.Add(path, new Config()
            {
                LocalPort = localPort,
                RemoteHost = remoteHost,
                RemotePort = remotePort,
                Recursive = recursive
            });

            StartListening(localPort);
        }

        public void Update(string path, int localPort = 0, string remoteHost = "", int remotePort = 0, bool recursive = false)
        {
            var config = _instanceConfig[path];

            config.Recursive = recursive; //TODO: check ob schon alle exposed nods der sub patche registriert sind

            if (remoteHost != "") config.RemoteHost = remoteHost;
            if (remotePort != 0)  config.RemotePort = remotePort;

            if (remoteHost != "" || remotePort != 0 || recursive)
            {
                _HDEHost.ExposedNodeService.Nodes.ToList().ForEach(node =>
                {
                    PinChanged(node.FindPin(PinNameFromNode(node)), new EventArgs());
                });
            }

            _instanceConfig[path] = config;

            if (localPort != 0 && config.LocalPort != localPort)
            {
                StopListening(config.LocalPort);
                config.LocalPort = localPort;
                _instanceConfig[path] = config;
                StartListening(config.LocalPort);
            }
        }

        public void RemovePatch(string path)
        {
            StopListening(_instanceConfig[path].LocalPort);
            _instanceConfig.Remove(path);
        }

        private void Listen(int port)
        {
            _receiver[port] = new OSCReceiver(port);

            try
            {
                _receiver[port].Connect();

                while (true)
                {
                    var data = _receiver[port].Receive();

                    if (data == null) continue;


                    if (data.IsBundle())
                    {
                        var messages = data.Values;
                        foreach (OSCMessage message in messages)
                        {
                            _queue.Add(new ChangeMessage()
                            {
                                Port = port,
                                Name = message.Address.Substring(1),
                                Value = message.Values
                            });
                        }
                    }
                    else
                    {
                        _queue.Add(new ChangeMessage()
                        {
                            Port = port,
                            Name = data.Address.Substring(1),
                            Value = data.Values
                        });
                    }
                }
            }
            catch (Exception e)
            {
                System.Diagnostics.Debug.WriteLine(e.Message);
                //_receiver[port].Close();
                return;
            }
        }

        public void ProcessQueue()
        {

            ChangeMessage message;

            while (_queue.TryTake(out message))
            {
                var port = message.Port;
                var name = message.Name;
                var data = message.Value;


                // go throw every path whos listener is on this port
                _instanceConfig.Where(c => c.Value.LocalPort == port).ToList().ForEach(kv =>
                {
                    var pins = new List<IPin2>();
                    var config = kv.Value;
                    var path = kv.Key;

                    //search only nodes in current patch
                    if (!config.Recursive)
                    {
                        _exposedNodes.Where(n => n.Value.Parent.GetNodePath(false) == path && n.Value.LabelPin.Spread == name).Select(n => n.Key).ToList().ForEach(pin => pins.Add(pin));
                    }
                    else  // search nodes in this an sub patches
                    {
                        _exposedNodes.Where(n => path.StartsWith(n.Value.Parent.GetNodePath(false)) && n.Value.LabelPin.Spread == name).Select(n => n.Key).ToList().ForEach(pin => pins.Add(pin));
                    }

                    if (pins.Count == 0) return;

                    var values = "";

                    foreach (var v in data)
                    {
                        if (v is float)
                            values += ((float)v).ToString(System.Globalization.CultureInfo.InvariantCulture) + ",";
                        else if (v is double)
                            values += ((double)v).ToString(System.Globalization.CultureInfo.InvariantCulture) + ",";
                        else
                            values += v.ToString() + ",";
                    }
                    values = values.TrimEnd(',');

                    pins.ForEach(pin=> pin.Spread = values);
                });
            }
        }
    }
}
