/*
 * Copyright (c) 2014-2026 GraphDefined GmbH <achim.friedland@graphdefined.com>
 * This file is part of LocalController <https://github.com/OpenChargingCloud/LocalController>
 *
 * Licensed under the Affero GPL license, Version 3.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.gnu.org/licenses/agpl.html
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

#region Usings

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.DNS;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Norn.NTS;

using cloud.charging.open.protocols.WWCP.NetworkingNode;

using OCPPv2_1_LC = cloud.charging.open.protocols.OCPPv2_1.LocalController;

using cloud.charging.open.protocols.WWCP.Node;
using cloud.charging.open.protocols.WWCP.Node.Logging;
using cloud.charging.open.protocols.WWCP.Node.Configuration;

using cloud.charging.open.LocalController.Configuration;
using cloud.charging.open.LocalController.Web;

#endregion

namespace cloud.charging.open.LocalController
{

    /// <summary>
    /// One local controller: the OCPP node it speaks through, the server its
    /// charging stations connect to, the line up to its CSMS, the JSON API at
    /// "/api" and the web interface at "/".
    /// </summary>
    /// <remarks>
    /// The web interface is a bundle of HTML, CSS and JavaScript built by
    /// webpack from Frontend/ and embedded into this assembly, so that the
    /// controller is one file to deploy and needs nothing installed beside it.
    /// The browser and the controller talk over the JSON API and one
    /// Server-Sent Events stream; nothing is rendered on the server.
    ///
    /// A local controller sits between a CSMS above it and the charging
    /// stations below, and is normally the only thing in a car park with a
    /// keyboard within reach of it. That is what this web interface is for: it
    /// is the one place where somebody can see what the box is doing without
    /// having a back end to ask.
    ///
    /// Everything around that - the log, the configuration file, name
    /// resolution and the time, the accounts, and the HTTP server with the web
    /// interface behind it - is the <see cref="WWCPNode"/> below, which a
    /// vehicle and a charging station are built on as well. What is here is
    /// what makes one of those a local controller.
    /// </remarks>
    public partial class LocalController : WWCPNode
    {

        #region Data

        /// <summary>
        /// The manifest resource prefix of the embedded frontend bundle
        /// (see the EmbedFrontend target of LocalController.csproj).
        /// </summary>
        public const String  HTTPRoot            = "cloud.charging.open.LocalController.HTTPRoot.";

        /// <summary>
        /// The TCP port the web interface listens on, unless another is given.
        /// </summary>
        /// <remarks>
        /// Next to the ports an OpenChargingCloud charging station uses (2348,
        /// 2349) and not one of them: a local controller and a station are
        /// often tried out on the same bench, and two web interfaces fighting
        /// over one socket is a confusing way to find that out. The node below
        /// has a port of its own for a node of no particular kind, and this one
        /// is handed to it rather than left to it.
        /// </remarks>
        public static new readonly IPPort  DefaultHTTPPort  = IPPort.Parse(2350);

        /// <summary>
        /// The port the charging stations connect to, as a sentence names it
        /// when it cannot be had.
        /// </summary>
        public static readonly NodePort  StationServerPort  = new ("The charging station server");

        /// <summary>
        /// What a line the libraries below write has to contain to be tagged,
        /// and with what: the table the debug bridge of a local controller
        /// reads by.
        /// </summary>
        /// <remarks>
        /// What a local controller overhears is mostly OCPP going past it in
        /// both directions, so the table leans that way: which side a line is
        /// about - the CSMS above or a charging station below - is worth more
        /// here than which layer it came from. None of the vehicle's ISO 15118
        /// and SLAC, which a controller never hears.
        /// </remarks>
        public static readonly IReadOnlyList<(String Needle, String Tag)> TraceTags = [
            ("ocpp",               "ocpp"),
            ("bootnotification",   "ocpp"),
            ("heartbeat",          "ocpp"),
            ("websocket",          "websocket"),
            ("http",               "http"),
            ("tls",                "tls"),
            ("certificate",        "tls"),
            ("dns",                "dns"),
            ("nts",                "nts"),
            ("ntp",                "nts"),
            ("csms",               "csms"),
            ("charging station",   "station"),
            ("chargingstation",    "station"),
            ("chargebox",          "station"),
            ("routing",            "routing"),
            ("forward",            "routing")
        ];

        private readonly  OCPPv2_1_LC.TestLocalControllerNode  lc01;

        #endregion

        #region Properties

        /// <summary>
        /// Who this local controller says it is when it speaks OCPP.
        /// </summary>
        /// <remarks>
        /// As it was read at the start. Unlike the name servers and the time
        /// server this is not changeable while running - see
        /// <see cref="OCPPConfiguration"/> for why an identification is a
        /// different kind of setting from an address.
        /// </remarks>
        public OCPPConfiguration      OCPP                   { get; }

        /// <summary>
        /// The OCPP 2.1 local controller node this controller speaks through.
        /// </summary>
        public OCPPv2_1_LC.TestLocalControllerNode  Node
            => lc01;

        /// <summary>
        /// The JSON API at "/api/".
        /// </summary>
        public LCHTTPAPI              API                    { get; }

        #endregion

        #region Constructor(s)

        /// <summary>
        /// Create a local controller with a web interface in front of it.
        /// Nothing listens yet: <see cref="WWCPNode.Start"/> does.
        /// </summary>
        /// <param name="DNSClient">The DNS client used by everything below.</param>
        /// <param name="NTSClient">The time client.</param>
        /// <param name="HTTPServer">An HTTP server to register within, or null to make one.</param>
        /// <param name="BasePath">What everything of this controller sits below; the root by default. Something else only where several of these programs share one HTTP server.</param>
        /// <param name="HTTPRootPath">The root path of the JSON API, "/api" below <paramref name="BasePath"/> by default.</param>
        /// <param name="ExtAPI">An HTTPExt API to sign in against, or null for one of this controller's own. Handing one in is what makes one sign-in open several of these programs at once.</param>
        /// <param name="HTTPHostname">The address to listen on; the loopback address by default.</param>
        /// <param name="HTTPPort">The TCP port to listen on.</param>
        /// <param name="AccountsPath">The directory the accounts live in between starts; "accounts" beside the process by default.</param>
        /// <param name="ConfigFile">Where everything this controller can be told in writing lives: one file, whose sections the node below and the controller each read for themselves; "configuration.json" beside the process by default.</param>
        /// <param name="OCPP">Who this controller says it is in OCPP, unless the configuration file says otherwise.</param>
        /// <param name="Frontend">Where the web interface comes from; the bundle embedded in this assembly by default.</param>
        /// <param name="CertificatesPath">The directory the certificate store of the node below lives in between starts.</param>
        /// <param name="Log">The event log; a new one by default.</param>
        /// <param name="LogToConsole">Whether the event log is also written to the console.</param>
        /// <param name="ConsoleLogLevel">What the console shows of it.</param>
        /// <param name="LogPath">The directory the log files are written to, or null to write none.</param>
        /// <param name="BridgeDebugLog">Whether what the libraries below write with DebugX ends up in the log.</param>
        /// <param name="TimeProvider">Where this controller reads the time; the system clock by default.</param>
        public LocalController(DNSClient?             DNSClient          = null,
                               NTSClient?             NTSClient          = null,
                               HTTPServer?            HTTPServer         = null,
                               HTTPPath?              BasePath           = null,
                               HTTPPath?              HTTPRootPath       = null,
                               HTTPExtAPI?            ExtAPI             = null,
                               IIPAddress?            HTTPHostname       = null,
                               IPPort?                HTTPPort           = null,
                               String?                AccountsPath       = null,
                               WWCPConfigFile?        ConfigFile         = null,
                               OCPPConfiguration?     OCPP               = null,
                               IStaticContentSource?  Frontend           = null,
                               String?                CertificatesPath   = null,
                               EventLog?              Log                = null,
                               Boolean                LogToConsole       = true,
                               LogLevel               ConsoleLogLevel    = LogLevel.Info,
                               String?                LogPath            = null,
                               Boolean                BridgeDebugLog     = true,
                               TimeProvider?          TimeProvider       = null)

            : base(Kind:               new NodeKind(
                                           Name:           "local controller",
                                           Tag:            "lc",
                                           Product:        "LocalController",

                                           // What the accounts of every local
                                           // controller that has ever run are in:
                                           // written into the accounts file at its
                                           // first start and read back at every
                                           // start since, so it is this and
                                           // nothing else.
                                           Organization:   "LocalController",

                                           LogFilePrefix:  "localcontroller"
                                       ),
                   Version:            typeof(LocalController).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
                   HTTPPort:           HTTPPort ?? DefaultHTTPPort,
                   HTTPHostname:       HTTPHostname,
                   HTTPServer:         HTTPServer,
                   BasePath:           BasePath,
                   HTTPRootPath:       HTTPRootPath,
                   ExtAPI:             ExtAPI,
                   AccountsPath:       AccountsPath,
                   Roles:              UserRole.All.Select(role => role.Name),
                   ConfigFile:         ConfigFile,
                   DNSClient:          DNSClient,
                   NTSClient:          NTSClient,
                   Frontend:           Frontend ?? new EmbeddedContentSource(HTTPRoot, typeof(LocalController).Assembly),
                   CertificatesPath:   CertificatesPath,
                   Log:                Log,
                   LogToConsole:       LogToConsole,
                   ConsoleLogLevel:    ConsoleLogLevel,
                   LogPath:            LogPath,
                   BridgeDebugLog:     BridgeDebugLog,
                   TraceTags:          TraceTags,
                   TimeProvider:       TimeProvider)

        {

            #region What the configuration file says about a local controller

            // Its own sections of the document the node below has already
            // read: the ones that reading passed over are the ones this is
            // for.
            if (!ControllerConfiguration.TryParse(ConfigurationDocument, out var configuration, out var problem))
                throw new InvalidOperationException($"'{this.ConfigFile.Path}': {problem} Repair or remove '{this.ConfigFile.Path}' and start again.");

            if (!configuration.IsEmpty)
                this.Log.Info($"Local controller configuration from '{this.ConfigFile.Path}': {configuration}.", "config");

            #endregion

            #region Who this local controller says it is

            this.OCPP = configuration.OCPP
                            ?? OCPP
                            ?? new OCPPConfiguration();

            #endregion

            #region The JSON API

            this.Log.Info(
                OwnsExtAPI
                    ? $"The accounts of this local controller are in '{this.ExtAPI.DatabaseFileName}', its HTTPExt API at '{this.ExtAPI.RootPath}'."
                    : $"This local controller signs in against accounts it shares, at '{this.ExtAPI.RootPath}'.",
                "web", "http"
            );

            // The JSON API at "/api", beside the web interface the node below
            // has already put at "/". The more specific of the two, so that an
            // unknown /api path never reaches the single-page-application
            // stub.
            this.API           = new LCHTTPAPI(
                                     HTTPServer:  this.HTTPServer,
                                     Controller:  this,
                                     ExtAPI:      this.ExtAPI,
                                     Log:         this.Log,
                                     APIPath:     this.HTTPRootPath,
                                     Version:     Version
                                 );

            #endregion

            #region The OCPP node

            lc01 = BuildOCPPNode(this.OCPP);

            // "this." and not for tidiness: the parameters of this constructor
            // shadow the properties of the same name, and the "Log" parameter
            // is null whenever the caller did not bring an event log of its own.
            this.Log.Info($"OCPP 2.1 local controller '{lc01.Id}' is set up as {lc01.VendorName} {lc01.Model}.", "ocpp");

            #endregion

            #region The server the charging stations connect to

            // After the node, because it is attached to it, and after the
            // configuration file, because what it listens on is written there.
            // Nothing listens yet: Start() does.
            BuildOCPPServer(configuration.OCPPServer);

            // And the line upwards. Nothing is dialled yet either, for the
            // same reason: Start() does.
            BuildCSMSConnection(configuration.CSMS);

            #endregion

        }

        #endregion


        #region (protected override) OnListening()

        /// <summary>
        /// The port the charging stations connect to, beside the web
        /// interface's.
        /// </summary>
        /// <remarks>
        /// Taken here, once the node below has its own port and before it
        /// calls itself started, so that a controller whose stations cannot
        /// reach it does not say that it is listening. A port that cannot be
        /// had is said as a <see cref="PortUnavailableException"/> naming the
        /// station server; the node lets go of the web interface's port again
        /// on the way out.
        /// </remarks>
        protected override Task OnListening()

            => StartOCPPServer();

        #endregion

        #region (protected override) OnStarted()

        /// <summary>
        /// What a local controller says and does once it is up: where its API
        /// is, and then the line up to its CSMS.
        /// </summary>
        protected override async Task OnStarted()
        {

            Log.Info($"The JSON API is at {APIURL}v1/status", "web", "http");

            // After the port below and the web interface, so that a controller
            // whose backend is unreachable is still a controller its charging
            // stations can reach - and one whoever is watching the Logs page
            // sees dialling out. A CSMS that does not answer is logged, not
            // fatal.
            await ConnectCSMS();

        }

        #endregion

        #region (protected override) OnStopping()

        /// <summary>
        /// End what this controller holds open before the server stops.
        /// </summary>
        /// <remarks>
        /// The event streams first, and that order is the whole point: every
        /// browser with the Logs page open holds a request that is waiting for
        /// the next log entry rather than for its socket, and the HTTP server
        /// waits for every request it started. Closing the sockets does not
        /// wake those, so they are ended here - whoever owns the server,
        /// because the streams are this controller's.
        ///
        /// Then the CSMS, hung up before the station port is closed: a station
        /// that is still connected has somewhere for its last messages to go.
        /// </remarks>
        protected override async Task OnStopping()
        {

            API.CloseEventStreams();

            await DisconnectCSMS();

            await StopOCPPServer();

        }

        #endregion


        #region ConfigurationJSON()

        /// <summary>
        /// What this local controller is made of, as the Configuration page of
        /// the web interface reads it: what the node below says of itself, and
        /// on top the controller, its OCPP identity, its station server and the
        /// assemblies it was built from.
        /// </summary>
        public override JObject ConfigurationJSON()
        {

            var json = base.ConfigurationJSON();

            // First, because it is the card the page leads with.
            json.AddFirst(new JProperty("controller", new JObject(
                              new JProperty("version",        Version),
                              new JProperty("createdAt",      CreatedAt.ToString("o")),
                              new JProperty("machine",        Environment.MachineName),
                              new JProperty("runtime",        Environment.Version.ToString()),
                              new JProperty("os",             Environment.OSVersion.ToString())
                          )));

            json.Add(new JProperty("ocpp",       new JObject(
                         new JProperty("version",          "2.1"),
                         new JProperty("role",             "Local Controller"),
                         new JProperty("id",               lc01.Id.ToString()),
                         new JProperty("vendor",           lc01.VendorName),
                         new JProperty("model",            lc01.Model),
                         new JProperty("serialNumber",     lc01.SerialNumber),
                         new JProperty("softwareVersion",  lc01.SoftwareVersion),
                         new JProperty("file",             ConfigFile.Path)
                     )));

            json.Add(new JProperty("stationServer", new JObject(
                         new JProperty("enabled",          OCPPServerEnabled),
                         new JProperty("running",          ocppServerStarted),
                         new JProperty("tls",              ocppServerTLS),
                         new JProperty("url",              OCPPServerURL),
                         new JProperty("securityProfiles", new JArray((ocppServerSettings.SecurityProfiles ?? []).Select(profile => (Int32) profile))),
                         new JProperty("stationLogins",    StationLogins.EnabledCount),
                         new JProperty("trustedChains",    ClientTrust.EnabledCount),
                         new JProperty("certificates",     ServerCertificates.Entries.Count)
                     )));

            json.Add(new JProperty("assemblies", new JArray(
                         AssemblyJSON<HTTPServer>                              ("Hermod"),
                         AssemblyJSON<NTSClient>                               ("Norn"),
                         AssemblyJSON<WWCPNode>                                ("WWCP Node"),
                         AssemblyJSON<OCPPv2_1_LC.TestLocalControllerNode>     ("OCPP 2.1")
                     )));

            return json;

        }

        #endregion


        #region (private) BuildOCPPNode(Configuration)

        /// <summary>
        /// The OCPP 2.1 node this local controller speaks through.
        /// </summary>
        /// <remarks>
        /// Every HTTP API the node brings of its own is switched off: this
        /// controller has one HTTP server, on one port, and a second one
        /// appearing on a port nobody chose would be a door into the same
        /// process that the sign-in in front of this one does not guard.
        ///
        /// Nothing is connected here. Building the node and dialling a CSMS are
        /// two different things, and the second one is not something a
        /// constructor should do on the way past.
        /// </remarks>
        private OCPPv2_1_LC.TestLocalControllerNode BuildOCPPNode(OCPPConfiguration Configuration)

            => new (

                   Id:                             NetworkingNode_Id.Parse(Configuration.NodeId     ?? OCPPConfiguration.DefaultNodeId),
                   VendorName:                     Configuration.VendorName                         ?? OCPPConfiguration.DefaultVendorName,
                   Model:                          Configuration.Model                              ?? OCPPConfiguration.DefaultModel,
                   SerialNumber:                   Configuration.SerialNumber,
                   SoftwareVersion:                Configuration.SoftwareVersion                    ?? Version,
                   Modem:                          null,
                   Description:                    I18NString.Empty,

                   HTTPAPI_Disabled:               true,
                   HTTPAPI_EventLoggingDisabled:   true,
                   HTTPDownloadAPI_Disabled:       true,
                   HTTPUploadAPI_Disabled:         true,
                   WebAPI_Disabled:                true,
                   NTSServer_Disabled:             true,

                   ControlWebSocketServer:         null,

                   DisableSendHeartbeats:          true,
                   DisableMaintenanceTasks:        true,

                   DNSClient:                      DNSClient

               );

        #endregion

        #region (private static) AssemblyJSON<T>(Name)

        private static JObject AssemblyJSON<T>(String Name)
        {

            var assembly = typeof(T).Assembly.GetName();

            return new JObject(
                       new JProperty("name",      Name),
                       new JProperty("assembly",  assembly.Name),
                       new JProperty("version",   assembly.Version?.ToString(3))
                   );

        }

        #endregion


        #region DisposeAsync()

        /// <summary>
        /// Stop listening, let go of what is this controller's own, and then
        /// of the rest.
        /// </summary>
        public override async ValueTask DisposeAsync()
        {

            // Stopped here as well as below: the stores may not go before the
            // station server that reads them has.
            await Stop();

            ServerCertificates?.Dispose();
            ClientTrust?       .Dispose();

            await base.DisposeAsync();

        }

        #endregion

    }

}
