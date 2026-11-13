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

using System.Collections.Concurrent;

using org.GraphDefined.Vanaheimr.Hermod.SSH;

using cloud.charging.open.protocols.WWCP.Node.Logging;
using cloud.charging.open.protocols.WWCP.Node.SecureShell;

#endregion

namespace cloud.charging.open.LocalController.OCPP
{

    /// <summary>
    /// The charging stations, as logins of this local controller's SSH server:
    /// a station signs in under its identification with one of its SSH keys,
    /// for OCPP over SSH and for what else a station has over its connection:
    /// its time and its names.
    /// </summary>
    /// <remarks>
    /// <b>The station logins decide, as over the WebSocket port.</b> A station
    /// that is listed, switched on, with a key of its own that is switched on
    /// and valid, in a group that is switched on and accepts SSH keys. The
    /// security profiles of the group are not asked: SSH is no OCPP security
    /// profile, see <see cref="AuthMethod.SSHKey"/>.
    ///
    /// <b>OCPP - and its time and its names, where its group gives them.</b>
    /// The SSH connection is the station's, and OCPP one channel of it: NTS
    /// key establishment (<see cref="NTSKE"/>) and DNS (<see cref="DNS"/>) are
    /// further channels of the same connection, under the same key. They are
    /// a grant of their own, which the station's login group makes
    /// (<see cref="LoginGroup.Channels"/>): a key admitted for OCPP does not
    /// obtain cookies or resolve names by itself. And only what the node
    /// serves now is named at all.
    ///
    /// <b>Nothing else, and nothing while OCPP is switched off.</b> The node
    /// confines every such login to the subsystems named here - no terminal,
    /// no command line, no SFTP, and no subsystem the node serves to its
    /// accounts - and none is named while OCPP over SSH is switched off, which
    /// is also when no station is let in at all. From where a station may
    /// have which of them is for the SSH access rules besides:
    /// "principal=station:* service=subsystem:...".
    ///
    /// <b>Asked again while a session lasts.</b> Every few seconds, and at once
    /// when the station logins change: a key taken away, a station or its
    /// group switched off, ends the session it opened.
    ///
    /// Only stations with an SSH key are logins here, so that a station that
    /// only ever signs in over the WebSocket port does not take a name away
    /// from an account of the same name on the SSH server.
    /// </remarks>
    public sealed class StationSSHSignIns : ISSHSignIns
    {

        #region Data

        /// <summary>
        /// What a charging station is to the SSH access rules: "station:cs001".
        /// </summary>
        public const String Station = "station";

        /// <summary>
        /// How seldom one refusal of one station is said again.
        /// </summary>
        public static readonly TimeSpan SayAgainAfter = TimeSpan.FromMinutes(1);

        /// <summary>
        /// NTS key establishment over SSH: see <see cref="LoginGroup.NTSKE"/>.
        /// </summary>
        public const String NTSKE = LoginGroup.NTSKE;

        /// <summary>
        /// DNS over SSH: see <see cref="LoginGroup.DNS"/>.
        /// </summary>
        public const String DNS   = LoginGroup.DNS;

        private readonly ChargingStationLogins                         logins;
        private readonly Func<Boolean>                                 served;
        private readonly Func<IEnumerable<String>>                     servedBesides;
        private readonly TimeProvider                                  timeProvider;
        private readonly ConcurrentDictionary<String, DateTimeOffset>  said = [];

        #endregion

        #region Properties

        /// <summary>
        /// "station".
        /// </summary>
        public String  PrincipalKind
            => Station;

        /// <summary>
        /// The subsystem the stations get.
        /// </summary>
        public String  Subsystem     { get; }

        #endregion

        #region Events

        /// <summary>
        /// A station's key was let in no further, for a reason somebody can put
        /// right - its group, its being switched off. An unknown key is not
        /// said here: the SSH server says every failed sign-in itself.
        /// </summary>
        public event Action<LogLevel, String>? OnNotice;

        #endregion

        #region Constructor(s)

        /// <summary>
        /// The charging stations of the given logins, as SSH logins for the
        /// given subsystem.
        /// </summary>
        /// <param name="Logins">The station logins.</param>
        /// <param name="Subsystem">The subsystem of OCPP over SSH.</param>
        /// <param name="Served">Whether OCPP over SSH is served now.</param>
        /// <param name="ServedBesides">The names of the subsystems the node serves now: of them a station gets those its login group gives it, and no other.</param>
        /// <param name="TimeProvider">The clock keys are held to.</param>
        public StationSSHSignIns(ChargingStationLogins  Logins,
                                 String                 Subsystem,
                                 Func<Boolean>          Served,
                                 TimeProvider?          TimeProvider = null,
                                 Func<IEnumerable<String>>? ServedBesides = null)
        {

            this.logins        = Logins;
            this.Subsystem     = Subsystem;
            this.served        = Served;
            this.servedBesides = ServedBesides ?? (() => []);
            this.timeProvider  = TimeProvider ?? System.TimeProvider.System;

        }

        #endregion


        #region Knows(Username)

        /// <summary>
        /// Whether the name is a charging station that has an SSH key.
        /// </summary>
        public Boolean Knows(String Username)

            => logins.TryGet(Username, out var login) &&
               login.HasSSHKey;

        #endregion

        #region AuthorizePublicKeyAsync(Request, CancellationToken)

        /// <summary>
        /// Whether the key lets the station in: see the remarks of this class.
        /// </summary>
        public ValueTask<Boolean> AuthorizePublicKeyAsync(SshPublicKeyAuthRequest  Request,
                                                          CancellationToken        CancellationToken = default)

            => ValueTask.FromResult(WhyNotLetIn(Request.Username, Request.PublicKeyBlob, Saying: true) is null);

        #endregion

        #region GetRestrictionsAsync(Request, CancellationToken)

        /// <summary>
        /// What the key's own line confines the session to - from= above all.
        /// The node confines it to OCPP besides, whatever this says.
        /// </summary>
        public ValueTask<SshSessionRestrictions> GetRestrictionsAsync(SshPublicKeyAuthRequest  Request,
                                                                      CancellationToken        CancellationToken = default)

            => ValueTask.FromResult(
                   logins.FindSSHKey(Request.Username, Request.PublicKeyBlob, timeProvider.GetUtcNow())?.Key.Key.Restrictions
                       ?? SshSessionRestrictions.None
               );

        #endregion

        #region SubsystemsFor(Username) / GroupsOf(Username)

        /// <summary>
        /// OCPP over SSH while it is served, with what the station's login
        /// group gives it besides, where the node serves that; and nothing
        /// otherwise.
        /// </summary>
        public IReadOnlySet<String> SubsystemsFor(String Username)
        {

            if (!served())
                return new HashSet<String>(StringComparer.Ordinal);

            var subsystems = new HashSet<String>(StringComparer.Ordinal) { Subsystem };

            if (logins.TryGet(Username, out var login) &&
                logins.GroupOf(login) is LoginGroup { Enabled: true, Channels.Count: > 0 } group)
            {

                var servedNow = new HashSet<String>(servedBesides(), StringComparer.Ordinal);

                // Only what a group may give at all, whatever a group says.
                foreach (var channel in group.Channels)
                    if (LoginGroup.KnownSSHChannels.Contains(channel) && servedNow.Contains(channel))
                        subsystems.Add(channel);

            }

            return subsystems;

        }

        /// <summary>
        /// The login group of the station: what "group:..." in the SSH access
        /// rules means for a station.
        /// </summary>
        public IReadOnlyList<String> GroupsOf(String Username)

            => logins.TryGet(Username, out var login)
                   ? [ login.GroupId ]
                   : [];

        #endregion

        #region WhyNoLongerIn(Session)

        /// <summary>
        /// Why the station of the given session may not go on, or null where it
        /// may: everything that let it in, asked again now.
        /// </summary>
        public String? WhyNoLongerIn(SshSessionInfo Session)
        {

            if (Session.PublicKeyBlob is not Byte[] key)
                return $"the charging station '{Session.Username}' did not sign in with an SSH key";

            return WhyNotLetIn(Session.Username, key, Saying: false);

        }

        #endregion


        #region (private) WhyNotLetIn(Id, PublicKeyBlob, Saying)

        /// <summary>
        /// Why the station may not sign in with this key now, or null where it
        /// may.
        /// </summary>
        private String? WhyNotLetIn(String   Id,
                                    Byte[]   PublicKeyBlob,
                                    Boolean  Saying)
        {

            if (!served())
                return "OCPP over SSH is switched off";

            if (!logins.TryGet(Id, out var login))
                return $"there is no charging station '{Id}' any more";

            var now = timeProvider.GetUtcNow();

            // An unknown key is a stranger's, and not worth more than the SSH
            // server's own line; a key of the station that is refused for what
            // somebody configured is worth saying why.
            if (login.FindSSHKey(PublicKeyBlob, now) is null)
                return $"the key {SshFingerprint.Sha256(PublicKeyBlob)} is no longer one the charging station '{Id}' signs in with - taken out, switched off or expired";

            String? why = null;

            if (!login.Enabled)
                why = $"the charging station '{Id}' is switched off here";

            else if (logins.GroupOf(login) is not LoginGroup group)
                why = $"the charging station '{Id}' belongs to the login group '{login.GroupId}', which this controller does not have";

            else if (!group.Enabled)
                why = $"the charging station '{Id}' is in the login group '{group.Id}', which is switched off";

            else if (!group.Allows(AuthMethod.SSHKey))
                why = $"the charging station '{Id}' is in the login group '{group.Id}', which does not accept SSH keys";

            if (why is not null && Saying)
                Say(Id, why, now);

            return why;

        }

        #endregion

        #region (private) Say(Id, Why, Now)

        /// <summary>
        /// Say why a station's key was refused - once a minute at most: an SSH
        /// client asks with the same key more than once per sign-in, and a
        /// station tries again and again.
        /// </summary>
        private void Say(String Id, String Why, DateTimeOffset Now)
        {

            var key = $"{Id}\n{Why}";

            if (said.TryGetValue(key, out var last) && Now - last < SayAgainAfter)
                return;

            said[key] = Now;

            OnNotice?.Invoke(LogLevel.Warning, $"A charging station signing in over SSH was turned away: {Why}.");

        }

        #endregion

    }

}
