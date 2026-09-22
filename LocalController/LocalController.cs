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
using org.GraphDefined.Vanaheimr.Hermod.Mail;
using NullMailer = org.GraphDefined.Vanaheimr.Hermod.SMTP.NullMailer;
using org.GraphDefined.Vanaheimr.Norn.NTS;
using org.GraphDefined.Vanaheimr.Norn.Monitoring;
using org.GraphDefined.Vanaheimr.Norn.TimeSync;

using cloud.charging.open.protocols.WWCP.NetworkingNode;

using OCPPv2_1_LC = cloud.charging.open.protocols.OCPPv2_1.LocalController;

using cloud.charging.open.LocalController.Configuration;
using cloud.charging.open.LocalController.Logging;
using cloud.charging.open.LocalController.Web;

#endregion

namespace cloud.charging.open.LocalController
{

    /// <summary>
    /// One local controller: the OCPP node it speaks through, the HTTP server
    /// in front of it, the JSON API at "/api" and the web interface at "/".
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
    /// </remarks>
    public partial class LocalController : IAsyncDisposable
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
        /// over one socket is a confusing way to find that out.
        /// </remarks>
        public static readonly IPPort DefaultHTTPPort = IPPort.Parse(2350);

        /// <summary>
        /// Where the accounts live, unless another directory is given.
        /// </summary>
        public const String  DefaultAccountsPath          = "accounts";

        /// <summary>
        /// The accounts themselves, inside that directory.
        /// </summary>
        public const String  DefaultAccountsDatabaseFile  = "users.db";

        /// <summary>
        /// Where the HTTPExt API answers: accounts, groups and API keys.
        /// </summary>
        /// <remarks>
        /// Beside "/api" rather than under it, because it is not this
        /// controller's API: it is Hermod's, with its own routes and its own
        /// vocabulary, and putting it under /api/v1 would promise that this
        /// controller versions it.
        /// </remarks>
        public static readonly HTTPPath  ExtAPIPath       = HTTPPath.Parse("/ext");

        /// <summary>
        /// The account made at a first start.
        /// </summary>
        public const String  DefaultAdminUser             = "root";

        /// <summary>
        /// The organization that account belongs to.
        /// </summary>
        /// <remarks>
        /// A box in a car park has no organizations to speak of, and this one
        /// exists because the HTTPExt API's sign-in refuses an account that is
        /// in none - "You do not have access to any organization!" - however
        /// right its password is. So there is exactly one, named after the
        /// thing it stands for.
        /// </remarks>
        public const String  DefaultOrganization          = "LocalController";

        /// <summary>
        /// The file of the bundle that is the web interface; its presence is
        /// what says there is one to serve at all.
        /// </summary>
        public const String  IndexFile           = "index.html";

        /// <summary>
        /// The icon of the bundle, which /favicon.ico is pointed at.
        /// </summary>
        public const String  FaviconSVG          = "favicon.svg";

        private readonly  DNSClient                       dnsClient;
        private           NTSClient                       ntsClient;
        private           TimeSourceGroup                 timeSources;
        private readonly  MeasurementEngine               timeEngine;

        /// <summary>
        /// The name servers this local controller would ask, whether or not
        /// name resolution is switched on at the moment.
        /// </summary>
        /// <remarks>
        /// Kept beside the DNS client because switching name resolution off is
        /// done by taking its servers away - which is what being switched off
        /// actually means, for everything holding that client and not only for
        /// the parts of this controller that remember to ask first. Switching
        /// it back on needs the list back, and this is where it waited.
        /// </remarks>
        private           IReadOnlyList<DNSServerConfig>  configuredDNSServers;

        /// <summary>
        /// Serialises changes to what this local controller is made of, so that
        /// two browsers saving at the same moment do not build half a
        /// controller each.
        /// </summary>
        private readonly  SemaphoreSlim                   reconfigureLock = new (1, 1);

        private readonly  HTTPServer                      httpServer;
        private readonly  HTTPPath                        httpRootPath;

        /// <summary>
        /// When this local controller last managed to check its clock, what it
        /// found, and against whom.
        /// </summary>
        /// <remarks>
        /// Three fields rather than one object because they are written from
        /// one place and read from another, and the alternative - digging them
        /// back out of the JSON of the last check - would make every reader
        /// depend on the shape of a diagnostic.
        /// </remarks>
        private           DateTimeOffset?                 lastTimeCheck;
        private           TimeSpan?                       lastTimeCheckOffset;

        /// <summary>
        /// How many time servers the last check asked, and how many of them
        /// answered well enough to count.
        /// </summary>
        /// <remarks>
        /// Both, because either alone is misleading: "4 answered" says nothing
        /// without knowing four of how many, and "4 asked" says nothing about
        /// whether the answer is worth anything.
        /// </remarks>
        private           Int32?                          lastTimeCheckAsked;
        private           Int32?                          lastTimeCheckAnswered;
        private           String?                         lastTimeCheckServer;

        /// <summary>
        /// The clock that makes this local controller check its own, when NTS
        /// is on.
        /// </summary>
        private           ITimer?                         timeCheckTimer;

        /// <summary>
        /// What the file said about the time client, kept because the parts of
        /// it that are not the client itself - how often to check, and what the
        /// operator claims about the server - are read long afterwards.
        /// </summary>
        private           NTSConfiguration?               ntsSettings;

        private readonly  ConsoleLog?                     consoleLog;
        private readonly  TraceBridge?                    traceBridge;

        private readonly  OCPPv2_1_LC.TestLocalControllerNode  lc01;

        private           Boolean                         started;

        #endregion

        #region Properties

        /// <summary>
        /// Everything that happens inside this local controller.
        /// </summary>
        public EventLog               Log                    { get; }

        /// <summary>
        /// Who may open the web interface: the accounts, the groups they are
        /// in, and the sessions and API keys they hold.
        /// </summary>
        public HTTPExtAPI             ExtAPI                 { get; }

        /// <summary>
        /// Whether those accounts are this local controller's own, or somebody else's
        /// that it was handed.
        /// </summary>
        /// <remarks>
        /// Handing one in is what makes one sign-in open several of these
        /// programs at once: the groups each of them makes as it starts land
        /// in one set of accounts, and the names overlap on purpose - an
        /// account in "systemadmin" is an administrator of every one of them.
        /// </remarks>
        public Boolean                OwnsExtAPI             { get; }

        /// <summary>
        /// Whether the HTTP server is this local controller's own, or one it was
        /// handed and shares with somebody else.
        /// </summary>
        /// <remarks>
        /// A shared server is started and stopped by whoever made it. One that
        /// started a server it did not make would take the same socket twice
        /// where several of these programs are on it, and one that stopped it
        /// would close the web interface of every other program registered
        /// within it.
        /// </remarks>
        public Boolean                OwnsHTTPServer         { get; }

        /// <summary>
        /// Everything of this local controller - its web interface, its JSON API and,
        /// where the accounts are its own, those too - sits below this.
        /// </summary>
        /// <remarks>
        /// The root, which is what a local controller on a port of its own wants and
        /// what it always used to be. It is something else only where several
        /// of these programs share one HTTP server and are told apart by the
        /// first path segment rather than by the port.
        /// </remarks>
        public HTTPPath               BasePath               { get; }

        /// <summary>
        /// The base path as it is written into a URL: the empty string at the
        /// root, and "/LocalController" or the like below one.
        /// </summary>
        /// <remarks>
        /// Its own property because the two forms are not interchangeable and
        /// the difference is exactly one character: <c>HTTPPath.Root</c> writes
        /// itself as "/", and "/" + "/index.html" is a URL nothing serves.
        /// </remarks>
        public String                 BasePathText
            => BasePath == HTTPPath.Root
                   ? ""
                   : BasePath.ToString().TrimEnd('/');

        /// <summary>
        /// The directory the accounts live in between starts.
        /// </summary>
        public String                 AccountsPath           { get; }

        /// <summary>
        /// Where everything this local controller can be told in writing lives
        /// between starts: its name resolution, its time source, its OCPP
        /// identification.
        /// </summary>
        public ControllerConfigFile   ConfigFile             { get; }

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
        /// How this local controller resolves names.
        /// </summary>
        public DNSClient              DNSClient
            => dnsClient;

        /// <summary>
        /// Where this local controller reads the time.
        /// </summary>
        /// <remarks>
        /// Replaced rather than reconfigured when it is pointed at another
        /// server: an NTS client is bound to its host at construction, and the
        /// cookies and keys it holds belong to that host and to no other.
        /// </remarks>
        public NTSClient              NTSClient
            => ntsClient;

        /// <summary>
        /// The time servers of this local controller, as a group.
        /// </summary>
        public TimeSourceGroup  TimeSources
            => timeSources;

        /// <summary>
        /// Whether this local controller resolves names at all.
        /// </summary>
        /// <remarks>
        /// Switched off by taking the name servers away from the DNS client, so
        /// that it is off for everything that was handed that client - not only
        /// for the parts of this controller that would have remembered to check
        /// a flag first. A query then fails at once and says why.
        /// </remarks>
        public Boolean                DNSEnabled             { get; private set; } = true;

        /// <summary>
        /// Whether this local controller may ask its time server.
        /// </summary>
        public Boolean                NTSEnabled             { get; private set; } = true;

        /// <summary>
        /// The password this local controller made up because there were no
        /// accounts, or null when accounts were already there. It is shown
        /// once, on the console, and kept nowhere but in its hash.
        /// </summary>
        /// <remarks>
        /// Set by <see cref="Start"/> rather than by the constructor, because
        /// creating the account is asynchronous and a constructor that waited
        /// on it would be a constructor that can deadlock.
        /// </remarks>
        public String?                GeneratedPassword      { get; private set; }

        /// <summary>
        /// Where the web interface comes from: this assembly, or a directory
        /// on disk.
        /// </summary>
        public IStaticContentSource   Frontend               { get; }

        /// <summary>
        /// The JSON API at "/api/".
        /// </summary>
        public LCHTTPAPI              API                    { get; }

        /// <summary>
        /// The web interface at "/", or null when no bundle was found to serve.
        /// </summary>
        public HTTPAPI?               WebInterface           { get; }

        /// <summary>
        /// The URL to open in a browser.
        /// </summary>
        public URL                    WebInterfaceURL        { get; }

        /// <summary>
        /// The JSON API as a browser would type it: the server and the API's
        /// root path, which already carries the base path, with a slash at the end.
        /// </summary>
        public URL                    APIURL                 { get; }

        /// <summary>
        /// The version of this local controller.
        /// </summary>
        public String                 Version                { get; }

        /// <summary>
        /// Where this local controller reads the time.
        /// </summary>
        /// <remarks>
        /// A local controller is the clock of everything below it: it is what
        /// a charging station without a time source of its own is told the time
        /// by, and what the records passing through it are stamped against. So
        /// the clock is something to be handed in rather than reached for. The
        /// system clock by default; an NTS-disciplined or a fake one where a
        /// test or a calibration says so.
        /// </remarks>
        public TimeProvider           TimeProvider           { get; }

        /// <summary>
        /// When this local controller was created, by its own clock.
        /// </summary>
        public DateTimeOffset         CreatedAt              { get; }

        #endregion

        #region Constructor(s)

        /// <summary>
        /// Create a local controller with a web interface in front of it.
        /// Nothing listens yet: <see cref="Start"/> does.
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
        /// <param name="ConfigFile">Where everything this controller can be told in writing lives; "configuration.json" beside the process by default.</param>
        /// <param name="OCPP">Who this controller says it is in OCPP, unless the configuration file says otherwise.</param>
        /// <param name="Frontend">Where the web interface comes from; the bundle embedded in this assembly by default.</param>
        /// <param name="Log">The event log; a new one by default.</param>
        /// <param name="LogToConsole">Whether the event log is also written to the console.</param>
        /// <param name="ConsoleLogLevel">What the console shows of it.</param>
        /// <param name="BridgeDebugLog">Whether what the libraries below write with DebugX ends up in the log.</param>
        /// <param name="TimeProvider">Where this controller reads the time; the system clock by default.</param>
        public LocalController(DNSClient?             DNSClient         = null,
                               NTSClient?             NTSClient         = null,
                               HTTPServer?            HTTPServer        = null,
                               HTTPPath?              BasePath          = null,
                               HTTPPath?              HTTPRootPath      = null,
                               HTTPExtAPI?            ExtAPI            = null,
                               IIPAddress?            HTTPHostname      = null,
                               IPPort?                HTTPPort          = null,
                               String?                AccountsPath      = null,
                               ControllerConfigFile?  ConfigFile        = null,
                               OCPPConfiguration?     OCPP              = null,
                               IStaticContentSource?  Frontend          = null,
                               EventLog?              Log               = null,
                               Boolean                LogToConsole      = true,
                               LogLevel               ConsoleLogLevel   = LogLevel.Info,
                               Boolean                BridgeDebugLog    = true,
                               TimeProvider?          TimeProvider      = null)
        {

            #region The clock, before anything that wants to know the time

            // First of all, and not for tidiness: the event log below stamps
            // every entry with this, so a clock set afterwards would leave the
            // log reading the system one - and a log on a different clock than
            // the controller it belongs to cannot be held against anything.
            this.TimeProvider  = TimeProvider ?? System.TimeProvider.System;
            this.CreatedAt     = this.TimeProvider.GetUtcNow();

            #endregion

            #region The log, next - everything below it may want to say something

            this.Version      = typeof(LocalController).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            this.Log          = Log ?? new EventLog(TimeProvider: this.TimeProvider);

            this.consoleLog   = LogToConsole
                                    ? new ConsoleLog(this.Log, ConsoleLogLevel)
                                    : null;

            // Attached before anything else is built, so that what the DNS
            // client, the HTTP server and the OCPP node say while they are
            // being made is already in the log a browser will see later.
            this.traceBridge  = BridgeDebugLog
                                    ? TraceBridge.Attach(this.Log)
                                    : null;

            this.Log.Notice($"Local controller v{this.Version} starting up.", "lc");

            #endregion

            #region Where the accounts live

            // Ending in a separator, because the HTTPExt API builds the paths
            // of its files by putting strings together rather than with
            // Path.Combine: a directory that does not end in one would give it
            // "...accountsUsersAPI" and not "...accounts/UsersAPI".
            this.AccountsPath = AccountsPath ?? DefaultAccountsPath;

            if (!this.AccountsPath.EndsWith(Path.DirectorySeparatorChar))
                this.AccountsPath += Path.DirectorySeparatorChar;

            #endregion

            #region What the configuration file says

            this.ConfigFile = ConfigFile ?? new ControllerConfigFile(ControllerConfigFile.DefaultFileName);

            ControllerConfiguration? configuration = null;

            if (this.ConfigFile.Exists)
            {

                // A file that is there but cannot be read is not something to
                // paper over with defaults: somebody wrote down what their
                // controller is and got it wrong, and quietly running as
                // something else instead would be worse than stopping.
                if (!this.ConfigFile.TryLoad(out configuration, out var configError))
                    throw new InvalidOperationException($"{configError} Repair or remove '{this.ConfigFile.Path}' and start again.");

                this.Log.Info($"Configuration from '{this.ConfigFile.Path}': {configuration}.", "config");

            }

            #endregion

            #region The clients everything below shares

            this.dnsClient             = DNSClient ?? new DNSClient();
            this.configuredDNSServers  = [.. dnsClient.DNSServers];

            // The clock goes to the time client too: a controller that reads
            // one clock itself and disciplines another would have two, which is
            // one more than anything below it can be told the time by.
            this.ntsClient     = NTSClient    ?? new NTSClient(
                                                     DomainName.Parse(NTSConfiguration.DefaultHostname),
                                                     Timeout:         TimeSpan.FromSeconds(10),
                                                     DNSClient:       dnsClient,
                                                     TimeProvider:    this.TimeProvider
                                                 );

            this.timeEngine    = new MeasurementEngine(
                                     new MonitoringConfig {
                                         DroneId       = "localController",
                                         NTPTimeout    = TimeSpan.FromSeconds(5),
                                         NTSKETimeout  = TimeSpan.FromSeconds(10)
                                     },
                                     this.TimeProvider
                                 );

            // A group of one until the file says otherwise, which is what a
            // controller that was handed a client and nothing else has.
            this.timeSources   = new TimeSourceGroup(
                                     "legal",
                                     [ new NTSServerEndpoint(
                                           this.ntsClient.Hostname,
                                           this.ntsClient.NTSKE_Port,
                                           this.ntsClient.NTP_Port
                                       ) ]
                                 );

            // Last, and that is the whole precedence rule: what this
            // constructor was handed holds until the file says otherwise, and
            // what the file does not mention is left exactly as it was.
            if (configuration?.DNS is not null)
                ApplyDNSConfiguration(configuration.DNS);

            if (configuration?.NTS is not null)
                ApplyNTSConfiguration(configuration.NTS);

            this.ntsSettings = configuration?.NTS;

            #endregion

            #region Who this local controller says it is

            this.OCPP = configuration?.OCPP
                            ?? OCPP
                            ?? new OCPPConfiguration();

            #endregion

            #region The HTTP server, the JSON API and the web interface

            var address        = HTTPHostname ?? IPv4Address.Localhost;
            var port           = HTTPPort     ?? DefaultHTTPPort;

            this.OwnsHTTPServer = HTTPServer is null;

            this.httpServer    = HTTPServer   ?? new HTTPServer(
                                                     IPAddress:       address,
                                                     TCPPort:         port,
                                                     HTTPServerName:  $"OpenChargingCloud LocalController v{Version}",
                                                     DNSClient:       dnsClient
                                                 );

            // The root unless somebody is putting several of these programs on
            // one server, where the first path segment is what tells them
            // apart. Everything below is relative to it, which is the whole
            // reason it is settled here and read rather than repeated.
            this.BasePath      = BasePath     ?? HTTPPath.Root;

            this.httpRootPath  = HTTPRootPath ?? this.BasePath + LCHTTPAPI.DefaultAPIPath;

            this.WebInterfaceURL = URL.Parse($"http://{address}:{port}{this.BasePath.ToString().TrimEnd('/')}/");

            // From the server rather than from the web interface's URL: the API's
            // root path already carries the base path, and behind a URL that ends
            // in the base path it would be named twice.
            this.APIURL          = URL.Parse($"http://{address}:{port}/{this.httpRootPath.ToString().Trim('/')}/");

            // 1) The HTTPExt API at "/ext". First of the three, because it is
            //    the one with a database behind it: whatever it finds wrong
            //    with its files, it should say so before a port is opened and
            //    before anybody is let in against accounts that were not read.
            this.OwnsExtAPI    = ExtAPI is null;

            this.ExtAPI        = ExtAPI ?? new HTTPExtAPI(
                                     HTTPServer:             httpServer,
                                     RootPath:               this.BasePath + (ExtAPIPath),
                                     HTTPServerName:         $"OpenChargingCloud LocalController v{Version}",
                                     HTTPServiceName:        $"OpenChargingCloud LocalController v{Version}",
                                     APIRobotEMailAddress:   EMailAddress.Parse("OpenChargingCloud LocalController Robot <robot@charging.cloud>"),
                                     APIRobotGPGPassphrase:  "",

                                     // Nothing here sends mail. A controller
                                     // that notifies by e-mail is told so by
                                     // whoever runs it, with a submission
                                     // client of their own; until then a
                                     // mailer that swallows what it is given
                                     // is better than one that quietly retries
                                     // against a host nobody configured.
                                     SMTPSubmissionClient:   new NullMailer(),
                                     DisableNotifications:   true,

                                     // The cookie has to reach "/api", and its
                                     // path would otherwise be the root path of
                                     // this API - "/ext" - so a browser signed
                                     // in at /ext/login would send nothing to
                                     // the API and look signed out everywhere
                                     // else.
                                     HTTPCookiePath:         "/",

                                     // A secure cookie is dropped by a browser
                                     // over plain HTTP, and a controller on a
                                     // bench is reached over plain HTTP. Tied
                                     // to the TLS the server is actually using
                                     // rather than switched off: on a box with
                                     // a certificate this stays on.
                                     UseSecureCookies:       false,

                                     // The shortest name a role of this controller has,
                                     // because that is what a group identification has
                                     // to be allowed to be. Hermod's own floor is four
                                     // characters and "cpo" is three, so the group
                                     // would be refused - by a returned result rather
                                     // than an exception, which is a refusal nobody is
                                     // obliged to notice - and the role it carries
                                     // could never be held by anybody.
                                     MinUserGroupIdLength:   (Byte) UserRole.All.Min(role => role.Name.Length),

                                     LoggingPath:            AccountsPath,
                                     DatabaseFileName:       DefaultAccountsDatabaseFile,

                                     // Left on, and that is what makes the
                                     // directory above: switching it off skips
                                     // the CreateDirectory that the accounts
                                     // file is written into, and the first
                                     // account created would fail on a path
                                     // that was never made.
                                     DisableLogging:         false
                                 );

            this.Log.Info(
                OwnsExtAPI
                    ? $"The accounts of this local controller are in '{this.ExtAPI.DatabaseFileName}', its HTTPExt API at '{this.ExtAPI.RootPath}'."
                    : $"This local controller signs in against accounts it shares, at '{this.ExtAPI.RootPath}'.",
                "web", "http"
            );

            // 2) The JSON API at "/api". Before the web interface, so that it
            //    is the more specific API and an unknown /api path never
            //    reaches the single-page-application stub below.
            this.API           = new LCHTTPAPI(
                                     HTTPServer:  httpServer,
                                     Controller:  this,
                                     ExtAPI:      this.ExtAPI,
                                     Log:         this.Log,
                                     APIPath:     httpRootPath,
                                     Version:     Version
                                 );

            // 3) The web interface at "/": the files of the bundle, and the
            //    single-page-application stub for every other page URL, so
            //    that a reload on /logs and a bookmark to it both work.
            this.Frontend      = Frontend ?? new EmbeddedContentSource(HTTPRoot, typeof(LocalController).Assembly);

            if (this.Frontend.TryGet(IndexFile, out _))
            {

                this.WebInterface = httpServer.AddHTTPAPI(this.BasePath);

                this.WebInterface.MapSinglePageApplication(
                    this.Frontend,
                    new SinglePageAppOptions {

                        // Three placeholders and not one. The bundle reads
                        // where it is and where its API is out of <meta> tags
                        // rather than assuming "/" and "/api/v1", because
                        // under a base path both of those are wrong - and a
                        // single-page application that guesses its own base
                        // path is one that works until somebody mounts it
                        // somewhere.
                        IndexTransform = html => html.
                                                     Replace("{{ServerVersion}}", $"v{Version}",         StringComparison.Ordinal).
                                                     Replace("{{BasePath}}",      BasePathText,          StringComparison.Ordinal).
                                                     Replace("{{APIBase}}",       $"{httpRootPath.ToString().TrimEnd('/')}/v1", StringComparison.Ordinal).
                                                     Replace("{{ExtBase}}",       this.ExtAPI.RootPath.ToString().TrimEnd('/'), StringComparison.Ordinal)

                    }
                );

                // Browsers ask for /favicon.ico whatever the page says, and a
                // bundle built by webpack carries an SVG. A literal route wins
                // over the catch-all, so this answers before the stub would -
                // and beats a 404 on every visit, which is a line in the log
                // and a broken icon in the tab.
                if (this.Frontend.TryGet(FaviconSVG, out _))
                    this.WebInterface.AddHandler(
                        HTTPPath.Parse("/favicon.ico"),
                        request => Task.FromResult(
                                       new HTTPResponse.Builder(request) {
                                           HTTPStatusCode  = HTTPStatusCode.TemporaryRedirect,
                                           Location        = Location.From(HTTPPath.Parse($"{BasePathText}/{FaviconSVG}")),
                                           CacheControl    = "public, max-age=3600"
                                       }.AsImmutable
                                   ),
                        HTTPMethod.GET
                    );

            }

            else
                this.Log.Error(
                    $"No web interface to serve ({this.Frontend.Description}): the JSON API answers, the browser gets nothing. " +
                    "Build the frontend (npm run build in Frontend/) or point the controller at a directory with --frontend.",
                    "web"
                );

            #region Every request, into the log

            httpServer.OnHTTPRequest  += (server, request, cancellationToken) => {

                // The event stream is one request that stays open for as long
                // as a browser has the page open; logging it would say nothing
                // and logging its response would say it at the wrong moment.
                if (!IsEventStream(request))
                    this.Log.Debug($"{request.HTTPMethod} {request.Path} from {request.RemoteSocket}", "http");

                return Task.CompletedTask;

            };

            // Only OnHTTPResponse, and not OnHTTPError beside it: Hermod raises
            // both for the same response, and one line per request is what a
            // log is for.
            httpServer.OnHTTPResponse += (server, request, response, cancellationToken) => {

                if (IsEventStream(request))
                    return Task.CompletedTask;

                var code = response.HTTPStatusCode.Code;

                this.Log.Log(
                    code >= 500 ? LogLevel.Error
                        // A 401 is how the web interface asks whether anybody
                        // is signed in, and the answer "nobody" is not a fault.
                        : code == 401 ? LogLevel.Debug
                        : code >= 400 ? LogLevel.Warning
                        : LogLevel.Debug,
                    $"{code} {response.HTTPStatusCode.Name} for {request.HTTPMethod} {request.Path}",
                    "http"
                );

                return Task.CompletedTask;

            };

            #endregion

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
            BuildOCPPServer(configuration?.OCPPServer);

            // And the line upwards. Nothing is dialled yet either, for the
            // same reason: Start() does.
            BuildCSMSConnection(configuration?.CSMS);

            #endregion

        }

        #endregion


        #region Start()

        /// <summary>
        /// Start listening.
        /// </summary>
        public async Task Start()
        {

            if (started)
                return;

            // Before the port opens, and that order is the point: a web
            // interface reachable before its accounts exist is a door with
            // nobody behind it.
            await EnsureAccounts();

            // Only where it is ours: a shared server is started by whoever
            // made it, and starting it again would take the same socket twice.
            if (OwnsHTTPServer)
                await httpServer.Start();

            await StartOCPPServer();

            // After the port below, so that a controller whose backend is
            // unreachable is still a controller its charging stations can
            // reach. A CSMS that does not answer is logged, not fatal.
            await ConnectCSMS();

            StartCheckingTheClock();

            started = true;

            Log.Notice($"The web interface is listening on {WebInterfaceURL}", "web", "http");
            Log.Info   ($"The JSON API is at {APIURL}v1/status", "web", "http");

        }

        #endregion

        #region Stop()

        /// <summary>
        /// Stop listening.
        /// </summary>
        public async Task Stop()
        {

            if (!started)
                return;

            Log.Notice("The local controller is shutting down.", "lc");

            timeCheckTimer?.Dispose();
            timeCheckTimer = null;

            // Before the server, and that order is the whole point: every
            // browser with the Logs page open holds a request that is waiting
            // for the next log entry rather than for its socket, and the HTTP
            // server waits for every request it started. Closing the sockets
            // does not wake those, so they are ended here first.
            API.CloseEventStreams();

            // Hung up before the port below is closed: a station that is
            // still connected has somewhere for its last messages to go.
            await DisconnectCSMS();

            await StopOCPPServer();

            // The event streams above are ended whoever owns the server,
            // because they are this local controller's; the socket is closed only where it
            // is this local controller's too.
            if (OwnsHTTPServer)
                await httpServer.Stop();

            started = false;

        }

        #endregion


        #region (private) EnsureAccounts()

        /// <summary>
        /// Make the three groups and, at a first start, the one account that
        /// is in the last of them.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The groups are made every start rather than only the first, because
        /// they are this controller's vocabulary and not somebody's data: a
        /// group deleted by hand would otherwise leave a role that can never be
        /// held again, and the routes asking for it would refuse everybody with
        /// no way to put it right.
        /// </para>
        /// <para>
        /// The account is made only when there is none at all. Nobody can sign
        /// in to a web interface whose accounts are empty, and an
        /// unauthenticated setup page would be a door of its own - so the
        /// password is made up here and shown once, on the console, to whoever
        /// started the process. It is never written down: what the accounts
        /// hold is the hash the HTTPExt API makes of it.
        /// </para>
        /// </remarks>
        private async Task EnsureAccounts()
        {

            // Read what is on disk first. The HTTPExt API writes its accounts
            // as it goes but does not read them back when it is built, so a
            // controller that skipped this would find no accounts at every
            // start, make a second root beside the first, and refuse the
            // password its owner already has.
            await ExtAPI.LoadDatabase();

            var firstStart  = !ExtAPI.Users.Any();

            IUser?  admin   = null;

            #region The one account, when there is none

            if (firstStart)
            {

                var password  = RandomExtensions.RandomString(24);
                var userId    = User_Id.Parse(DefaultAdminUser);

                // An account in no organization is refused at the sign-in -
                // "You do not have access to any organization!" - however right
                // its password is. Hence the single organization, which a box
                // in a car park has no other use for.
                var organization  = await ExtAPI.CreateOrganizationIfNotExists(
                                              Organization_Id.Parse(DefaultOrganization),
                                              I18NString.Create(Languages.en, DefaultOrganization)
                                          );

                if (organization is not Organization controllerOrganization)
                    throw new InvalidOperationException("The organization of this local controller could not be created, and an account outside one cannot sign in.");

                // CreateUser rather than AddUser: the password is set from
                // inside the OnAdded callback, where the user already has its
                // API back-reference, and that is the only place the password
                // store can be reached. AddUser followed by ChangePassword
                // looks equivalent and writes the account without one - which
                // is an account nobody can sign in to, and nothing says so.
                admin         = await ExtAPI.CreateUser(
                                          userId,
                                          I18NString.Create(Languages.en, DefaultAdminUser),
                                          SimpleEMailAddress.Parse($"{DefaultAdminUser}@localhost"),
                                          User2OrganizationEdgeLabel.IsAdmin,
                                          controllerOrganization,
                                          Password:                  password,

                                          // Nothing is sent and nobody is told:
                                          // a controller has no mail server, no
                                          // second user to notify, and the one
                                          // account it makes is announced on the
                                          // console it was started from.
                                          SkipDefaultNotifications:  true,
                                          SkipNewUserEMail:          true,
                                          SkipNewUserNotifications:  true,

                                          // Without this nobody can sign in, and
                                          // nothing says why: the sign-in paths
                                          // require an accepted EULA and refuse a
                                          // correct password without one. There is
                                          // no agreement to show here - whoever
                                          // started the process owns the box - so
                                          // it is accepted at the moment the
                                          // account is made.
                                          AcceptedEULA:              TimeProvider.GetUtcNow().AddSeconds(-1),

                                          IsAuthenticated:           true
                                      );

                if (admin is null)
                    throw new InvalidOperationException("The account of this local controller could not be created, so nobody could sign in to it.");

                GeneratedPassword = password;

                Log.Notice($"No accounts were found, so '{DefaultAdminUser}' was made up and put in the {UserRole.SystemAdmin.Name} group.",
                           "web", "auth");

            }

            #endregion

            #region The three groups

            foreach (var role in UserRole.All)
            {

                if (ExtAPI.TryGetUserGroup(role.GroupId, out _))
                    continue;

                var added = await ExtAPI.AddUserGroup(
                                      new UserGroup(
                                          role.GroupId,
                                          I18NString.Create(Languages.en, role.Name)
                                      )
                                  );

                // Looked at, and that is the point: this answers with a result
                // rather than throwing, so a group it declined to make would
                // otherwise leave a role nobody can ever hold - and every route
                // asking for it refusing everybody, with nothing anywhere to
                // say why. Better to stop before the port opens.
                if (added.Result != CommandResult.Success)
                    throw new InvalidOperationException(
                              $"The user group '{role.GroupId}' of this controller could not be made: " +
                              $"{added.Description.FirstText()} A role without its group is a role nobody can hold."
                          );

            }

            #endregion

            #region The one account joins the one group that can fix the rest

            // Through AddUserToUserGroup, which writes a command of its own.
            // Putting the edge on the group object before storing it looks
            // equivalent and is not: what AddUserGroup writes is the group,
            // and a group's stored form does not carry its members - so the
            // membership was there until the next start and gone after it,
            // which is the worst shape a permission can have.
            if (admin is not null)
            {

                if (!ExtAPI.TryGetUser     (admin.Id,                     out var storedAdmin) ||
                    !ExtAPI.TryGetUserGroup(UserRole.SystemAdmin.GroupId, out var adminGroup)  ||
                     storedAdmin is not User      user ||
                     adminGroup  is not UserGroup group)
                {
                    throw new InvalidOperationException(
                              $"The account of this local controller could not be put in the {UserRole.SystemAdmin.Name} group, " +
                               "so the one account it has would be allowed to do nothing at all."
                          );
                }

                var joined = await ExtAPI.AddUserToUserGroup(
                                       user,
                                       User2UserGroupEdgeLabel.IsAdmin,
                                       group
                                   );

                // Looked at for the same reason as the group above: this
                // answers with a result too, and a membership it declined to
                // write leaves the one account able to do nothing at all -
                // with a password about to be printed that opens nothing.
                // A different result type from AddUserGroup's, and so a
                // different question: IsSuccess rather than Result.
                if (!joined.IsSuccess)
                    throw new InvalidOperationException(
                              $"The account '{DefaultAdminUser}' could not be put in the {UserRole.SystemAdmin.Name} group: " +
                              $"{joined.ErrorDescription?.FirstText()} It would be able to do nothing at all."
                          );

            }

            #endregion

        }

        #endregion

        #region ConfigurationJSON()

        /// <summary>
        /// What this local controller is made of, as the Configuration page of
        /// the web interface reads it.
        /// </summary>
        /// <remarks>
        /// Read-only: it answers "what am I running", not "change it". Nothing
        /// here is a secret - the accounts appear as the path they live at and
        /// the route to sign in, and never as anything about a password.
        /// </remarks>
        public JObject ConfigurationJSON()

            => new (

                   new JProperty("controller", new JObject(
                       new JProperty("version",        Version),
                       new JProperty("createdAt",      CreatedAt.ToString("o")),
                       new JProperty("machine",        Environment.MachineName),
                       new JProperty("runtime",        Environment.Version.ToString()),
                       new JProperty("os",             Environment.OSVersion.ToString())
                   )),

                   new JProperty("http",       new JObject(
                       new JProperty("serverName",     httpServer.HTTPServerName),
                       new JProperty("url",            WebInterfaceURL.ToString()),
                       new JProperty("apiPath",        httpRootPath.ToString()),
                       new JProperty("running",        started),
                       new JProperty("frontend",       Frontend.Description),
                       new JProperty("webInterface",   WebInterface is not null)
                   )),

                   new JProperty("web",        new JObject(
                       new JProperty("accountsPath",   AccountsPath),
                       new JProperty("signInAt",       $"{ExtAPIPath.ToString().TrimEnd('/')}/login"),
                       new JProperty("users",          ExtAPI.Users.     Count()),
                       new JProperty("groups",         ExtAPI.UserGroups.Count()),
                       new JProperty("cookie",         ExtAPI.SessionCookieName.ToString()),
                       new JProperty("maxLifetime",    ExtAPI.MaxSignInSessionLifetime.ToString())
                   )),

                   new JProperty("log",        new JObject(
                       new JProperty("capacity",       Log.Capacity),
                       new JProperty("entries",        Log.Count),
                       new JProperty("lastId",         Log.LastId),
                       new JProperty("debugBridge",    traceBridge is not null),
                       new JProperty("console",        consoleLog is not null),
                       new JProperty("tags",           new JArray(Log.KnownTags))
                   )),

                   new JProperty("time",       new JObject(
                       new JProperty("nts",            ntsClient.Hostname.ToString()),
                       new JProperty("now",            TimeProvider.GetUtcNow().ToString("o"))
                   )),

                   new JProperty("ocpp",       new JObject(
                       new JProperty("version",          "2.1"),
                       new JProperty("role",             "Local Controller"),
                       new JProperty("id",               lc01.Id.ToString()),
                       new JProperty("vendor",           lc01.VendorName),
                       new JProperty("model",            lc01.Model),
                       new JProperty("serialNumber",     lc01.SerialNumber),
                       new JProperty("softwareVersion",  lc01.SoftwareVersion),
                       new JProperty("file",             ConfigFile.Path)
                   )),

                   new JProperty("stationServer", new JObject(
                       new JProperty("enabled",          OCPPServerEnabled),
                       new JProperty("running",          ocppServerStarted),
                       new JProperty("tls",              ocppServerTLS),
                       new JProperty("url",              OCPPServerURL),
                       new JProperty("securityProfiles", new JArray((ocppServerSettings.SecurityProfiles ?? []).Select(profile => (Int32) profile))),
                       new JProperty("stationLogins",    StationLogins.EnabledCount),
                       new JProperty("trustedChains",    ClientTrust.EnabledCount),
                       new JProperty("certificates",     ServerCertificates.Entries.Count)
                   )),

                   new JProperty("assemblies", new JArray(
                       AssemblyJSON<HTTPServer>                              ("Hermod"),
                       AssemblyJSON<NTSClient>                               ("Norn"),
                       AssemblyJSON<OCPPv2_1_LC.TestLocalControllerNode>     ("OCPP 2.1")
                   ))

               );

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

                   DNSClient:                      dnsClient

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

        #region (private static) IsEventStream(Request)

        /// <summary>
        /// Whether this request is a browser hanging on the event stream.
        /// </summary>
        private static Boolean IsEventStream(HTTPRequest Request)
            => Request.Path.ToString().EndsWith("/events", StringComparison.Ordinal);

        #endregion

        #region DisposeAsync()

        /// <summary>
        /// Stop listening and let go of the console and the debug bridge.
        /// </summary>
        public async ValueTask DisposeAsync()
        {

            await Stop();

            traceBridge?.Dispose();
            consoleLog? .Dispose();

            ServerCertificates?.Dispose();
            ClientTrust?       .Dispose();

            reconfigureLock.Dispose();

            GC.SuppressFinalize(this);

        }

        #endregion

    }

}
