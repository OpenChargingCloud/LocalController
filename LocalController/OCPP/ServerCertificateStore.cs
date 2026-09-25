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

using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.EC;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math.EC.Multiplier;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;

using BCx509 = Org.BouncyCastle.X509;

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.PKI;

using cloud.charging.open.protocols.WWCP.Node.Logging;
using cloud.charging.open.LocalController.Web;

#endregion

namespace cloud.charging.open.LocalController.OCPP
{

    /// <summary>
    /// The keys and certificates the charging station server authenticates
    /// with, in a directory of their own.
    /// </summary>
    /// <remarks>
    /// <b>Why more than one.</b> A certificate has to be replaced before it
    /// expires, and the replacement is frequently issued days before it becomes
    /// valid. So both live here at once, and which of them is presented is
    /// decided per connection rather than at a restart: the new one is uploaded
    /// the day it arrives, and takes over by itself the moment it is allowed to.
    ///
    /// <b>The private key is generated here and never leaves.</b> There is no
    /// import: a key that arrived over the network, through a browser and into
    /// a file has been in three places that a key should not have been in, and
    /// no amount of care afterwards takes it back out of them. What leaves is
    /// the signing request, which is a public document.
    ///
    /// <b>When nothing is valid, the last certificate keeps being served.</b>
    /// A charging station that checks certificates will refuse it and say so;
    /// one that does not will go on charging cars. Refusing to serve anything
    /// would take the site down in both cases, which is the worse of the two
    /// failures for a box in a car park - so this says so loudly in the log
    /// instead, and keeps the door open.
    /// </remarks>
    public sealed class ServerCertificateStore : IDisposable
    {

        #region Data

        /// <summary>
        /// The directory beside the process, when nobody says otherwise.
        /// </summary>
        public const String  DefaultDirectoryName  = "ocpp-server-keys";

        /// <summary>
        /// The key algorithms that may be asked for - see
        /// <see cref="KeyAlgorithm"/>, which is where they and their several
        /// awkwardnesses live.
        /// </summary>
        public static IReadOnlyList<KeyAlgorithm> Algorithms
            => KeyAlgorithm.All;

        /// <summary>
        /// The algorithm a key is generated with when nothing says otherwise.
        /// </summary>
        public const String  DefaultAlgorithm      = KeyAlgorithm.DefaultId;

        /// <summary>
        /// How long before a certificate expires this store starts saying so.
        /// </summary>
        /// <remarks>
        /// Thirty days is roughly how long it takes to get a certificate out of
        /// an organisation that has to ask somebody. The later warnings are
        /// there for when the first one was read by nobody.
        /// </remarks>
        public static readonly Int32[] WarnDaysBefore = [ 30, 14, 7, 3, 1 ];

        /// <summary>
        /// The longest a subject may be written.
        /// </summary>
        public const Int32   MaxSubjectLength      = 200;

        private readonly Object                                   updateLock  = new ();
        private readonly Dictionary<String, ServerCertificateEntry>  entries  = [];

        /// <summary>
        /// The public key of every entry, which is what an uploaded certificate
        /// is matched against to find out whose it is.
        /// </summary>
        private readonly Dictionary<String, Byte[]>               publicKeys  = [];

        /// <summary>
        /// The chain that is being presented, and the entry it came from.
        /// </summary>
        private ServerCertificateChain?                           served;
        private String?                                           servedId;

        #endregion

        #region Properties

        /// <summary>
        /// The directory the keys and certificates live in.
        /// </summary>
        public String        Path          { get; }

        /// <summary>
        /// The clock that decides which certificate is in its window.
        /// </summary>
        /// <remarks>
        /// Handed in rather than reached for, and for a sharper reason than
        /// usual: this is the one place in the controller where a wrong clock
        /// silently does the wrong thing. A box that boots believing it is 1970
        /// would find no certificate valid and a box a year fast would switch
        /// to one that nobody else accepts yet.
        /// </remarks>
        public TimeProvider  TimeProvider  { get; }

        /// <summary>
        /// Everything in the directory, newest key first.
        /// </summary>
        public IReadOnlyList<ServerCertificateEntry> Entries
        {
            get
            {
                lock (updateLock)
                    return [.. entries.Values.OrderByDescending(entry => entry.CreatedAt)];
            }
        }

        /// <summary>
        /// Whether there is any certificate at all - which is what decides
        /// whether this server can speak TLS.
        /// </summary>
        public Boolean HasCertificate
        {
            get
            {
                lock (updateLock)
                    return entries.Values.Any(entry => entry.Certificate is not null &&
                                                       entry.CanBePresented);
            }
        }

        /// <summary>
        /// The identification of the certificate currently being presented, or
        /// null while none has been chosen.
        /// </summary>
        public String? ServedId
        {
            get
            {
                lock (updateLock)
                    return servedId;
            }
        }

        #endregion

        #region Events

        /// <summary>
        /// Something happened here that belongs in the log of the controller:
        /// a certificate taken into use, a certificate that expired with no
        /// replacement, a file that could not be read.
        /// </summary>
        /// <remarks>
        /// An event rather than an event log handed in, so that this store can
        /// be built and tested without one - and so that what level a sentence
        /// is logged at stays the decision of the thing that owns the log.
        /// </remarks>
        public event Action<LogLevel, String>? OnNotice;

        #endregion

        #region Constructor(s)

        /// <summary>
        /// The keys and certificates in the given directory; it need not exist
        /// yet.
        /// </summary>
        public ServerCertificateStore(String?        Path           = null,
                                      TimeProvider?  TimeProvider   = null)
        {

            this.Path          = System.IO.Path.GetFullPath(Path ?? DefaultDirectoryName);
            this.TimeProvider  = TimeProvider ?? System.TimeProvider.System;

            Reload();

        }

        #endregion


        #region Reload()

        /// <summary>
        /// Read the directory again, from nothing.
        /// </summary>
        /// <remarks>
        /// A file that cannot be read is skipped with a line in the log rather
        /// than thrown: one unreadable key must not stop a controller that has
        /// three others, and the one certificate that is currently keeping a
        /// car park running is frequently one of the three.
        /// </remarks>
        public void Reload()
        {

            lock (updateLock)
            {

                foreach (var entry in entries.Values)
                    entry.Dispose();

                entries.Clear();
                publicKeys.Clear();

                served    = null;
                servedId  = null;

                if (!Directory.Exists(Path))
                    return;

                foreach (var keyFile in Directory.GetFiles(Path, "*.key.pem").OrderBy(file => file))
                {

                    var id = System.IO.Path.GetFileName(keyFile);
                    id = id[..id.IndexOf(".key.pem", StringComparison.Ordinal)];

                    try
                    {

                        if (TryLoadEntry(id, out var entry, out var publicKey, out var problem))
                        {
                            entries   [id] = entry;
                            publicKeys[id] = publicKey;
                        }

                        else
                            OnNotice?.Invoke(LogLevel.Error, $"The key '{id}' in '{Path}' could not be read and is being ignored: {problem}");

                    }
                    catch (Exception e)
                    {
                        OnNotice?.Invoke(LogLevel.Error, $"The key '{id}' in '{Path}' could not be read and is being ignored: {e.Message}");
                    }

                }

            }

        }

        #endregion

        #region TryCreateKey(Subject, ReachableAs, Algorithm, out Id, out CSR, out Error)

        /// <summary>
        /// Generate a key pair and the signing request that goes with it.
        /// </summary>
        /// <remarks>
        /// The request is written beside the key so that it can be downloaded
        /// again: it is frequently wanted a second time, by somebody who is not
        /// the person who pressed the button, and asking for a new key because
        /// the old request was lost would throw away a key for a filing
        /// mistake.
        /// </remarks>
        /// <param name="Subject">The common name to ask for, e.g. "lc001.example.org".</param>
        /// <param name="ReachableAs">The names and addresses the charging stations reach this controller under - a certificate without them in it is a certificate they will refuse.</param>
        /// <param name="Algorithm">One of <see cref="Algorithms"/>.</param>
        public Boolean TryCreateKey(String                            Subject,
                                    IEnumerable<String>               ReachableAs,
                                    String?                           Algorithm,
                                    [NotNullWhen(true)]  out String?  Id,
                                    [NotNullWhen(true)]  out String?  CSR,
                                    [NotNullWhen(false)] out String?  Error)
        {

            Id     = null;
            CSR    = null;
            Error  = null;

            #region What was asked for

            var algorithm = KeyAlgorithm.Find(Algorithm ?? DefaultAlgorithm);

            if (algorithm is null)
            {
                Error = $"'{Algorithm}' is not a key this local controller generates ({String.Join(", ", KeyAlgorithm.All.Select(one => one.Id))}).";
                return false;
            }

            var subject = Subject?.Trim() ?? "";

            if (subject.Length == 0)
            {
                Error = "A certificate needs a subject; the name the charging stations reach this controller under is the one to use.";
                return false;
            }

            if (subject.Length > MaxSubjectLength)
            {
                Error = $"The subject may be at most {MaxSubjectLength} characters long.";
                return false;
            }

            // Plain text is what somebody types, so it is read as a common
            // name; a full distinguished name is taken as written.
            var distinguishedName = subject.Contains('=')
                                        ? subject
                                        : $"CN={subject.Replace("\\", "\\\\").Replace(",", "\\,").Replace("=", "\\=")}";

            X509Name subjectName;
            String   subjectText;

            try
            {
                // Read by .NET first because it is stricter about what somebody
                // may type, then handed on in the form Bouncy Castle signs
                // with - so the message about a bad subject is the readable one
                // and the request is still built by the half that knows every
                // algorithm.
                var x500     = new X500DistinguishedName(distinguishedName);
                subjectText  = x500.Name;
                subjectName  = new X509Name(x500.Name);
            }
            catch (Exception e)
            {
                Error = $"'{subject}' is not a usable certificate subject: {e.Message}";
                return false;
            }

            var names = ReachableAs.Select(name => name.Trim()).
                                    Where (name => name.Length > 0).
                                    Distinct(StringComparer.OrdinalIgnoreCase).
                                    ToArray();

            if (names.Length == 0)
            {
                Error = "A certificate has to name what this controller is reachable as, or no charging station will accept it. " +
                        "Fill in 'reachable as' before making a request.";
                return false;
            }

            #endregion

            AsymmetricCipherKeyPair pair;

            try
            {
                pair = algorithm.Generate();
            }
            catch (Exception e)
            {
                Error = $"A {algorithm.Name} key could not be generated: {e.Message}";
                return false;
            }

            var publicKey  = SubjectPublicKeyInfoFactory.CreateSubjectPublicKeyInfo(pair.Public).GetDerEncoded();
            var id         = KeyId(publicKey);

            lock (updateLock)
            {

                if (entries.ContainsKey(id))
                {
                    // Two identical keys is not something that happens by
                    // chance; it happens when a directory was copied.
                    Error = $"A key with the identification '{id}' is already here.";
                    return false;
                }

                #region The signing request

                String csr;

                try
                {
                    // Built by Hermod, which knows every algorithm in the list
                    // above and what each one of them may honestly claim for
                    // itself - an Ed448 or an ML-DSA key asking to be allowed
                    // to encipher is a request a strict certificate authority
                    // is entitled to refuse.
                    csr = PKIFactory.GenerateCertificateSigningRequest(
                              pair,
                              subjectName,
                              algorithm,
                              names
                          ).ToPEM();
                }
                catch (Exception e)
                {
                    Error = $"The signing request could not be made: {e.Message}";
                    return false;
                }

                #endregion

                #region Written down, the key first and with its own permissions

                var createdAt = TimeProvider.GetUtcNow();

                try
                {

                    CreateDirectory();

                    OwnerOnlyFile.Write(
                        FilePath(id, "key.pem"),
                        PemEncoding.WriteString(
                            "PRIVATE KEY",
                            PrivateKeyInfoFactory.CreatePrivateKeyInfo(pair.Private).GetDerEncoded()
                        ) + Environment.NewLine
                    );

                    File.WriteAllText(FilePath(id, "csr.pem"), csr);

                    File.WriteAllText(
                        FilePath(id, "json"),
                        new JObject(
                            new JProperty("id",         id),
                            new JProperty("algorithm",  algorithm.Id),
                            new JProperty("createdAt",  createdAt.ToString("o")),
                            new JProperty("subject",    subjectText),
                            new JProperty("reachableAs", new JArray(names))
                        ).ToString(Formatting.Indented) + Environment.NewLine
                    );

                }
                catch (Exception e)
                {
                    Error = $"The key could not be written to '{Path}': {e.Message}";
                    return false;
                }

                #endregion

                var entry = new ServerCertificateEntry(id, algorithm.Name, createdAt, subjectText);

                entries   [id] = entry;
                publicKeys[id] = publicKey;

                Id   = id;
                CSR  = csr;

            }

            OnNotice?.Invoke(LogLevel.Notice, $"A new {algorithm.Name} key '{id}' was generated and a signing request for {String.Join(", ", names)} is waiting to be collected.");

            return true;

        }

        #endregion

        #region TryReadCSR(Id, out CSR, out Error)

        /// <summary>
        /// The signing request of a key, to be handed to a certificate
        /// authority.
        /// </summary>
        public Boolean TryReadCSR(String                            Id,
                                  [NotNullWhen(true)]  out String?  CSR,
                                  [NotNullWhen(false)] out String?  Error)
        {

            CSR    = null;
            Error  = null;

            if (!Known(Id))
            {
                Error = $"There is no key '{Id}' here.";
                return false;
            }

            var path = FilePath(Id, "csr.pem");

            if (!File.Exists(path))
            {
                Error = $"The signing request of '{Id}' is no longer in '{Path}'.";
                return false;
            }

            try
            {
                CSR = File.ReadAllText(path);
                return true;
            }
            catch (Exception e)
            {
                Error = $"The signing request of '{Id}' could not be read: {e.Message}";
                return false;
            }

        }

        #endregion

        #region TryAddCertificate(PEM, ReachableAs, out Id, out Warnings, out Error)

        /// <summary>
        /// Take a certificate that came back from a certificate authority,
        /// together with whatever intermediates were sent with it.
        /// </summary>
        /// <remarks>
        /// <b>Everything that can be checked is checked here and not at the
        /// moment of the switch.</b> A certificate uploaded today to take over
        /// in two days switches over at whatever hour it becomes valid, with
        /// nobody watching; a mistake found then is found by the charging
        /// stations. So a certificate whose key is not here, or that is already
        /// expired, is refused now, while somebody is looking at the screen.
        ///
        /// What is reported but not refused is everything that may legitimately
        /// look wrong from in here: a chain this box cannot verify may be from
        /// a private authority every charging station on the site knows, and
        /// names that do not match may be a 'reachable as' list that is out of
        /// date rather than a wrong certificate.
        /// </remarks>
        /// <param name="PEM">The certificate, and any intermediates, as PEM.</param>
        /// <param name="ReachableAs">What the certificate ought to be valid for.</param>
        public Boolean TryAddCertificate(String                              PEM,
                                         IEnumerable<String>                 ReachableAs,
                                         [NotNullWhen(true)]  out String?    Id,
                                         out IReadOnlyList<String>           Warnings,
                                         [NotNullWhen(false)] out String?    Error)
        {

            Id        = null;
            Warnings  = [];
            Error     = null;

            #region What was uploaded

            var uploaded = new X509Certificate2Collection();

            try
            {
                uploaded.ImportFromPem(PEM);
            }
            catch (Exception e)
            {
                Error = $"This is not a certificate: {e.Message}";
                return false;
            }

            if (uploaded.Count == 0)
            {
                Error = "There is no certificate in this file. What is expected is PEM - the '-----BEGIN CERTIFICATE-----' kind - " +
                        "with the certificate of this controller first and any intermediates after it.";
                return false;
            }

            #endregion

            var subject   = "";
            var notAfter  = default(DateTimeOffset);

            lock (updateLock)
            {

                #region Whose is it?

                X509Certificate2? leaf    = null;
                String?           leafId  = null;

                foreach (var certificate in uploaded)
                {

                    var spki = certificate.PublicKey.ExportSubjectPublicKeyInfo();

                    var match = publicKeys.FirstOrDefault(pair => pair.Value.AsSpan().SequenceEqual(spki));

                    if (match.Key is not null)
                    {
                        leaf    = certificate;
                        leafId  = match.Key;
                        break;
                    }

                }

                if (leaf is null || leafId is null)
                {
                    Error = "None of the certificates in this file belongs to a key of this local controller. " +
                            "A certificate is only usable here if it answers a signing request made here.";
                    return false;
                }

                #endregion

                var now = TimeProvider.GetUtcNow();

                #region Already expired

                if (new DateTimeOffset(leaf.NotAfter.ToUniversalTime()) <= now)
                {
                    Error = $"This certificate expired on {leaf.NotAfter.ToUniversalTime():yyyy-MM-dd} and can never be used. " +
                            $"By the clock of this local controller it is now {now:yyyy-MM-dd}.";
                    return false;
                }

                notAfter = new DateTimeOffset(leaf.NotAfter.ToUniversalTime());

                #endregion

                #region The intermediates, in the order they are sent in

                var pool           = uploaded.Cast<X509Certificate2>().
                                              Where(certificate => !certificate.RawData.AsSpan().SequenceEqual(leaf.RawData)).
                                              ToList();

                var intermediates  = OrderTowardsTheRoot(leaf, pool);

                #endregion

                #region Written down

                try
                {

                    CreateDirectory();

                    var pem = String.Join(
                                  Environment.NewLine,
                                  new[] { leaf }.Concat(intermediates).
                                      Select(certificate => PemEncoding.WriteString("CERTIFICATE", certificate.RawData))
                              ) + Environment.NewLine;

                    File.WriteAllText(FilePath(leafId, "cert.pem"), pem);

                }
                catch (Exception e)
                {
                    Error = $"The certificate could not be written to '{Path}': {e.Message}";
                    return false;
                }

                #endregion

                // Read back the way it will be read at the next start, rather
                // than assembled from what is still in hand: a certificate that
                // cannot be loaded from its own file is one that would be gone
                // after a restart, and that is worth finding out now.
                if (!TryLoadEntry(leafId, out var entry, out var publicKey, out var problem))
                {
                    Error = $"The certificate was written but cannot be read back: {problem}";
                    return false;
                }

                if (entries.TryGetValue(leafId, out var replaced))
                    replaced.Dispose();

                entries   [leafId] = entry;
                publicKeys[leafId] = publicKey;

                CheckEntry(entry, ReachableAs, now);

                Id        = leafId;
                Warnings  = [.. entry.Warnings];
                subject   = entry.Certificate?.Subject ?? leafId;

            }

            OnNotice?.Invoke(
                LogLevel.Notice,
                $"A certificate for '{subject}' was taken in, valid until {notAfter:yyyy-MM-dd}." +
                (Warnings.Count > 0 ? $" {String.Join(" ", Warnings)}" : "")
            );

            return true;

        }

        #endregion

        #region TryRemove(Id, out Error)

        /// <summary>
        /// Throw a key, its request and its certificate away.
        /// </summary>
        /// <remarks>
        /// The one that is currently being presented is refused: a charging
        /// station server without a certificate stops speaking TLS, and doing
        /// that by deleting a file is not a decision anybody means to make.
        /// Upload the replacement first.
        /// </remarks>
        public Boolean TryRemove(String                            Id,
                                 [NotNullWhen(false)] out String?  Error)
        {

            Error = null;

            lock (updateLock)
            {

                if (!entries.TryGetValue(Id, out var entry))
                {
                    Error = $"There is no key '{Id}' here.";
                    return false;
                }

                if (servedId == Id)
                {
                    Error = $"'{Id}' is the certificate this server is presenting right now. " +
                             "Upload the one that is to replace it first, then remove this one.";
                    return false;
                }

                try
                {
                    foreach (var extension in new[] { "key.pem", "csr.pem", "cert.pem", "json" })
                    {
                        var path = FilePath(Id, extension);
                        if (File.Exists(path))
                            File.Delete(path);
                    }
                }
                catch (Exception e)
                {
                    Error = $"'{Id}' could not be removed from '{Path}': {e.Message}";
                    return false;
                }

                entry.Dispose();
                entries.Remove(Id);
                publicKeys.Remove(Id);

            }

            OnNotice?.Invoke(LogLevel.Notice, $"The key '{Id}' and everything belonging to it was removed from '{Path}'.");

            return true;

        }

        #endregion

        #region Select()

        /// <summary>
        /// The certificate chain to present, decided afresh - which is what
        /// makes a certificate uploaded ahead of time take over by itself.
        /// </summary>
        /// <remarks>
        /// <b>The rule.</b> Of the certificates that are in their validity
        /// window right now, the one that became valid last; where two became
        /// valid at the same moment, the one that lasts longer; and where those
        /// are equal too, the one whose thumbprint sorts first - so that two
        /// starts of the same controller never disagree.
        ///
        /// When nothing is in its window, whatever was last presented goes on
        /// being presented, and on a fresh start that is the one that expired
        /// most recently. See the remarks on this class for why that is better
        /// than presenting nothing.
        /// </remarks>
        public ServerCertificateChain? Select()
        {

            var now = TimeProvider.GetUtcNow();

            lock (updateLock)
            {

                // Not merely "has a certificate": one this machine cannot
                // present would turn every handshake into a failure, which is
                // worse than the expired certificate below.
                var usable = entries.Values.Where(entry => entry.Certificate is not null &&
                                                           entry.CanBePresented).ToArray();

                if (usable.Length == 0)
                    return null;

                var chosen = usable.Where(entry => entry.IsValidAt(now)).
                                    OrderByDescending(entry => entry.NotBefore!.Value).
                                    ThenByDescending (entry => entry.NotAfter!. Value).
                                    ThenBy           (entry => entry.Certificate!.Thumbprint, StringComparer.Ordinal).
                                    FirstOrDefault();

                if (chosen is null)
                {

                    // Nothing is valid. Keep whatever is already going out;
                    // a restart in this state takes the one that lasted longest.
                    if (served is not null)
                        return served;

                    chosen = usable.OrderByDescending(entry => entry.NotAfter!.Value).First();

                    OnNotice?.Invoke(
                        LogLevel.Critical,
                        $"No server certificate is valid right now - by this controller's clock it is {now:yyyy-MM-dd HH:mm}'Z'. " +
                        $"'{chosen.Certificate!.Subject}' (expired {chosen.NotAfter:yyyy-MM-dd}) is being presented anyway so that the " +
                         "charging stations are not locked out. Upload a current certificate."
                    );

                }

                if (servedId != chosen.Id || served is null)
                {

                    served    = new ServerCertificateChain(chosen.Certificate!, chosen.Intermediates.Cast<X509Certificate2>());
                    servedId  = chosen.Id;

                    OnNotice?.Invoke(
                        LogLevel.Notice,
                        $"The charging station server is now presenting '{chosen.Certificate!.Subject}' " +
                        $"({chosen.Id}, valid until {chosen.NotAfter:yyyy-MM-dd}, {chosen.Intermediates.Count} intermediate(s))."
                    );

                }

                return served;

            }

        }

        #endregion

        #region CheckExpiry(ReachableAs)

        /// <summary>
        /// Say what is about to run out, and how the certificates look against
        /// the names this controller is currently reachable under.
        /// </summary>
        /// <remarks>
        /// This is the half of planning a replacement that the store can do:
        /// the other half is somebody reading it. So the sentences are written
        /// to be read in a log by a person who was not thinking about
        /// certificates a moment ago.
        /// </remarks>
        public void CheckExpiry(IEnumerable<String> ReachableAs)
        {

            var now   = TimeProvider.GetUtcNow();
            var names = ReachableAs.ToArray();

            lock (updateLock)
            {

                foreach (var entry in entries.Values)
                    CheckEntry(entry, names, now);

                var valid = entries.Values.Where(entry => entry.IsValidAt(now)).ToArray();

                // Nothing valid is the loudest case there is, and it used to be
                // the quietest: an early return here meant the one state worth
                // shouting about was the one that said nothing.
                if (valid.Length == 0)
                {

                    if (entries.Values.Any(entry => entry.Certificate is not null))
                        OnNotice?.Invoke(
                            LogLevel.Critical,
                            $"No server certificate of the charging station server is valid - by this controller's clock it is " +
                            $"{now:yyyy-MM-dd}. The charging stations that check certificates are being turned away. " +
                             "Upload a current certificate."
                        );

                    return;

                }

                var last      = valid.Max(entry => entry.NotAfter!.Value);
                var remaining = last - now;

                // Only against the certificate that lasts longest, and only
                // when nothing takes over from it: a certificate expiring next
                // week with its replacement already uploaded is exactly what a
                // planned replacement looks like, and warning about it is how a
                // warning gets ignored.
                var replacement = entries.Values.Any(entry => entry.Certificate is not null &&
                                                              entry.NotAfter > last);

                if (replacement)
                    return;

                var days = (Int32) Math.Floor(remaining.TotalDays);

                if (days <= WarnDaysBefore.Max())
                    OnNotice?.Invoke(
                        days <= 3 ? LogLevel.Critical : days <= 14 ? LogLevel.Error : LogLevel.Warning,
                        $"The server certificate of the charging station server runs out in {days} day(s), on {last:yyyy-MM-dd}, " +
                         "and nothing is here to take over from it. A replacement can be uploaded now and will be used by itself " +
                         "from the day it becomes valid."
                    );

            }

        }

        #endregion

        #region ToJSON()

        /// <summary>
        /// The keys and certificates, as the web interface reads them.
        /// </summary>
        public JObject ToJSON()
        {

            var now = TimeProvider.GetUtcNow();

            lock (updateLock)

                return new JObject(

                    new JProperty("directory",   Path),
                    new JProperty("now",         now.ToString("o")),
                    new JProperty("servedId",    servedId),

                    new JProperty("entries",     new JArray(
                        entries.Values.OrderByDescending(entry => entry.CreatedAt).
                                       Select(entry => entry.ToJSON(now, entry.Id == servedId))
                    )),

                    new JProperty("algorithms",  new JArray(
                        Algorithms.Select(algorithm => algorithm.ToJSON())
                    )),

                    // No import, and the page should say so rather than leave
                    // somebody looking for the button.
                    new JProperty("canImportPrivateKeys", false)

                );

        }

        #endregion


        #region (private) TryLoadEntry(Id, out Entry, out PublicKey, out Error)

        /// <summary>
        /// One key, what is known about it, and its certificate when it has one.
        /// </summary>
        private Boolean TryLoadEntry(String                                       Id,
                                     [NotNullWhen(true)] out ServerCertificateEntry?  Entry,
                                     [NotNullWhen(true)] out Byte[]?                  PublicKey,
                                     [NotNullWhen(false)] out String?                 Error)
        {

            Entry      = null;
            PublicKey  = null;
            Error      = null;

            #region What is known about the key

            var algorithm  = DefaultAlgorithm;
            var createdAt  = TimeProvider.GetUtcNow();
            var subject    = "";

            var metaPath   = FilePath(Id, "json");

            if (File.Exists(metaPath))
            {
                try
                {

                    var meta = JObject.Parse(File.ReadAllText(metaPath));

                    algorithm  = meta.Value<String>("algorithm") ?? DefaultAlgorithm;
                    subject    = meta.Value<String>("subject")   ?? "";

                    if (DateTimeOffset.TryParse(meta.Value<String>("createdAt"),
                                                System.Globalization.CultureInfo.InvariantCulture,
                                                System.Globalization.DateTimeStyles.RoundtripKind,
                                                out var parsed))
                        createdAt = parsed;

                }
                catch (Exception e)
                {
                    Error = $"'{metaPath}' could not be read: {e.Message}";
                    return false;
                }
            }

            var kind = KeyAlgorithm.Find(algorithm);

            if (kind is null)
            {
                Error = $"'{algorithm}' is not a key algorithm this local controller knows.";
                return false;
            }

            #endregion

            #region The key itself

            String keyPEM;

            try
            {
                keyPEM = File.ReadAllText(FilePath(Id, "key.pem"));
            }
            catch (Exception e)
            {
                Error = $"the private key could not be read: {e.Message}";
                return false;
            }

            AsymmetricKeyParameter  privateKey;
            Byte[]                  publicKey;

            try
            {

                // Whatever kind it is - the encoding says so, so nothing here
                // has to be told which of a dozen algorithms to expect.
                privateKey  = PrivateKeyFactory.CreateKey(PemEncoding.Find(keyPEM) is PemFields fields
                                                              ? Convert.FromBase64String(keyPEM[fields.Base64Data])
                                                              : throw new FormatException("this is not a PEM private key"));

                publicKey   = SubjectPublicKeyInfoFactory.CreateSubjectPublicKeyInfo(
                                  PublicKeyOf(privateKey)
                              ).GetDerEncoded();

            }
            catch (Exception e)
            {
                Error = $"the private key is not a usable {kind.Name} key: {e.Message}";
                return false;
            }

            PublicKey = publicKey;

            var entry = new ServerCertificateEntry(Id, kind.Name, createdAt, subject);

            #endregion

            #region The certificate, when there is one

            var certificatePath = FilePath(Id, "cert.pem");

            if (File.Exists(certificatePath))
            {

                var collection = new X509Certificate2Collection();

                try
                {
                    collection.ImportFromPem(File.ReadAllText(certificatePath));
                }
                catch (Exception e)
                {
                    Error = $"'{certificatePath}' is not a certificate: {e.Message}";
                    return false;
                }

                if (collection.Count == 0)
                {
                    Error = $"'{certificatePath}' holds no certificate.";
                    return false;
                }

                // The leaf is the one the key belongs to, wherever in the file
                // it happens to sit.
                var leaf = collection.Cast<X509Certificate2>().
                                      FirstOrDefault(certificate => certificate.PublicKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(publicKey));

                if (leaf is null)
                {
                    Error = $"'{certificatePath}' holds no certificate belonging to this key.";
                    return false;
                }

                try
                {
                    entry.Certificate = WithPrivateKey(leaf, privateKey);
                }
                catch (Exception e)
                {

                    // Not a reason to refuse the certificate: it is a perfectly
                    // good one, and the platform underneath is what cannot hold
                    // it. Kept, said out loud, and passed over when the server
                    // chooses what to present - because the day this runs
                    // somewhere else, or on a newer runtime, it may work.
                    entry.CanBePresented      = false;
                    entry.PresentationProblem = $"This machine cannot make a usable TLS certificate out of a {kind.Name} key: {e.Message} " +
                                                 "The certificate is kept, but it cannot be presented to a charging station from here.";

                }

                // Loading it is only half the question. A P-521 or an ML-DSA
                // certificate loads perfectly well and then finds no TLS stack
                // willing to negotiate with it, which is something only a
                // handshake finds out - so one is done, once per kind of key.
                if (entry.Certificate is not null &&
                    !KeyAlgorithm.CanBePresented(kind.Id, entry.Certificate))
                {

                    entry.CanBePresented      = false;
                    entry.PresentationProblem = $"A {kind.Name} certificate cannot be presented over TLS by this machine - the handshake fails. " +
                                                 "It is kept, and will be used the day the platform underneath can serve it.";

                }

                foreach (var intermediate in OrderTowardsTheRoot(
                                                 leaf,
                                                 collection.Cast<X509Certificate2>().
                                                            Where(certificate => !certificate.RawData.AsSpan().SequenceEqual(leaf.RawData)).
                                                            ToList()))
                {
                    entry.Intermediates.Add(intermediate);
                }

            }

            #endregion

            Entry = entry;
            return true;

        }

        #endregion

        #region (private) CheckEntry(Entry, ReachableAs, Now)

        /// <summary>
        /// What is worth saying about one certificate, recomputed from scratch.
        /// </summary>
        private static void CheckEntry(ServerCertificateEntry  Entry,
                                       IEnumerable<String>     ReachableAs,
                                       DateTimeOffset          Now)
        {

            Entry.Warnings.Clear();

            // First, and before the check below that there is a certificate at
            // all: where the platform cannot hold this kind of key, there is no
            // .NET certificate to look at - so a check that began by returning
            // on a missing one would swallow the only sentence that explains
            // why it is missing.
            if (Entry.PresentationProblem is not null)
                Entry.Warnings.Add(Entry.PresentationProblem);

            if (Entry.Certificate is null)
                return;

            #region Does it cover what the charging stations dial?

            var covered  = Entry.SubjectAlternativeNames().ToArray();

            var missing  = ReachableAs.Select(name => name.Trim()).
                                       Where (name => name.Length > 0).
                                       Where (name => !covered.Any(alternative => Covers(alternative, name))).
                                       ToArray();

            if (missing.Length > 0)
                Entry.Warnings.Add(
                    covered.Length == 0
                        ? "This certificate names nothing it is valid for, so charging stations that check it will refuse it."
                        : $"This certificate is not valid for {String.Join(", ", missing)} - it covers {String.Join(", ", covered)}."
                );

            #endregion

            #region Does the chain hold up from here?

            var selfSigned = Entry.Certificate.SubjectName.RawData.AsSpan().SequenceEqual(Entry.Certificate.IssuerName.RawData);

            if (!selfSigned && Entry.Intermediates.Count == 0)
                Entry.Warnings.Add(
                    "No intermediate certificates were uploaded with this one. Charging stations that do not already " +
                    "know the issuer will not be able to build a chain to it."
                );

            if (selfSigned)
                Entry.Warnings.Add("This certificate signed itself, so only a charging station that was told to trust it specifically will accept it.");

            #endregion

            #region Is it in its window?

            if (Entry.NotBefore > Now)
                Entry.Warnings.Add($"Not valid until {Entry.NotBefore:yyyy-MM-dd HH:mm}'Z'; it will be taken into use by itself then.");

            else if (Entry.NotAfter < Now)
                Entry.Warnings.Add($"Expired on {Entry.NotAfter:yyyy-MM-dd}.");

            #endregion

        }

        #endregion

        #region (private static) Covers(Name, Wanted)

        /// <summary>
        /// Whether a name in a certificate covers a name a charging station
        /// dials, wildcards included.
        /// </summary>
        /// <remarks>
        /// A wildcard stands for exactly one label, which is what TLS says and
        /// not what most people expect: "*.example.org" is "a.example.org" and
        /// is not "a.b.example.org", and is not "example.org" either.
        /// </remarks>
        private static Boolean Covers(String Name, String Wanted)
        {

            if (String.Equals(Name, Wanted, StringComparison.OrdinalIgnoreCase))
                return true;

            if (!Name.StartsWith("*.", StringComparison.Ordinal))
                return false;

            var suffix = Name[1..];

            if (!Wanted.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return false;

            // Exactly one label in front of the suffix, and a real one.
            var head = Wanted[..^suffix.Length];

            return head.Length > 0 && !head.Contains('.');

        }

        #endregion

        #region (private static) OrderTowardsTheRoot(Leaf, Pool)

        /// <summary>
        /// The intermediates between a certificate and a root, in the order TLS
        /// wants them: each one signing the one before it.
        /// </summary>
        /// <remarks>
        /// The order matters to stricter clients, and what arrives in an upload
        /// is whatever order the certificate authority happened to write. A
        /// root that came along is dropped: it is either already trusted, in
        /// which case it is bytes on the wire that change nothing, or it is not,
        /// in which case sending it changes nothing either.
        /// </remarks>
        private static List<X509Certificate2> OrderTowardsTheRoot(X509Certificate2        Leaf,
                                                                  List<X509Certificate2>  Pool)
        {

            var ordered  = new List<X509Certificate2>();
            var current  = Leaf;

            while (true)
            {

                var issuer = Pool.FirstOrDefault(candidate => candidate.SubjectName.RawData.AsSpan().
                                                                  SequenceEqual(current.IssuerName.RawData));

                if (issuer is null)
                    break;

                Pool.Remove(issuer);

                // A root signs itself, and is not sent.
                if (issuer.SubjectName.RawData.AsSpan().SequenceEqual(issuer.IssuerName.RawData))
                    break;

                ordered.Add(issuer);
                current = issuer;

            }

            return ordered;

        }

        #endregion

        #region (private static) WithPrivateKey(Certificate, Key)

        /// <summary>
        /// A certificate with its private key attached, in a form the platform
        /// can actually present.
        /// </summary>
        /// <remarks>
        /// Through PKCS#12, and built by Bouncy Castle rather than by .NET: a
        /// certificate whose key was attached in memory is handed to the
        /// platform's TLS stack without one, and .NET has no key object at all
        /// for an Ed448 or an ML-DSA key to attach. Bouncy Castle can write
        /// every one of them into a PKCS#12 blob, and loading that back is the
        /// one door every kind of key goes through.
        ///
        /// It throws where the platform will not take the result - which is
        /// information and not a fault: see the caller.
        /// </remarks>
        private static X509Certificate2 WithPrivateKey(X509Certificate2        Certificate,
                                                       AsymmetricKeyParameter  PrivateKey)
        {

            var store       = new Pkcs12StoreBuilder().Build();
            var bouncy      = new BCx509.X509CertificateParser().ReadCertificate(Certificate.RawData);
            var entry       = new X509CertificateEntry(bouncy);

            store.SetCertificateEntry(bouncy.SubjectDN.ToString(), entry);
            store.SetKeyEntry        (bouncy.SubjectDN.ToString(), new AsymmetricKeyEntry(PrivateKey), [ entry ]);

            using var blob  = new MemoryStream();

            var password    = Guid.NewGuid().ToString("N");

            store.Save(blob, password.ToCharArray(), new SecureRandom());

            return X509CertificateLoader.LoadPkcs12(
                       blob.ToArray(),
                       password,
                       X509KeyStorageFlags.Exportable
                   );

        }

        #endregion

        #region (private static) PublicKeyOf(PrivateKey) / KeyId

        /// <summary>
        /// The public half of a private key, whatever kind it is.
        /// </summary>
        /// <remarks>
        /// Bouncy Castle has no one method for this: an RSA private key carries
        /// the modulus and exponent that make up the public one, an elliptic
        /// curve key is a scalar that has to be multiplied by the generator,
        /// and the newer kinds simply hand theirs over. So one branch each,
        /// and a sentence rather than a silent null for anything else.
        /// </remarks>
        private static AsymmetricKeyParameter PublicKeyOf(AsymmetricKeyParameter PrivateKey)

            => PrivateKey switch {

                   RsaPrivateCrtKeyParameters rsa
                       => new RsaKeyParameters(false, rsa.Modulus, rsa.PublicExponent),

                   ECPrivateKeyParameters ec
                       => new ECPublicKeyParameters(
                              ec.AlgorithmName,
                              new FixedPointCombMultiplier().Multiply(ec.Parameters.G, ec.D),
                              ec.Parameters
                          ),

                   Ed25519PrivateKeyParameters ed25519  => ed25519.GeneratePublicKey(),
                   Ed448PrivateKeyParameters   ed448    => ed448.  GeneratePublicKey(),
                   MLDsaPrivateKeyParameters   mldsa    => mldsa.  GetPublicKey(),
                   SlhDsaPrivateKeyParameters  slhdsa   => slhdsa. GetPublicKey(),

                   _ => throw new NotSupportedException($"{PrivateKey.GetType().Name} is not a private key this local controller knows how to read.")

               };

        /// <summary>
        /// What a key is called: where its public key hashes to.
        /// </summary>
        /// <remarks>
        /// Derived from the key rather than made up, so that a signing request
        /// and the certificate that answers it cannot end up filed under
        /// different names - and so that the same key uploaded twice is
        /// recognised as the same key.
        /// </remarks>
        private static String KeyId(Byte[] SubjectPublicKeyInfo)
            => Convert.ToHexStringLower(SHA256.HashData(SubjectPublicKeyInfo).AsSpan(0, 8));

        #endregion

        #region (private) FilePath / Known / CreateDirectory

        private String FilePath(String Id, String Extension)
            => System.IO.Path.Combine(Path, $"{Id}.{Extension}");

        private Boolean Known(String Id)
        {
            lock (updateLock)
                return entries.ContainsKey(Id);
        }

        /// <summary>
        /// The directory, readable by its owner alone where that can be said.
        /// </summary>
        private void CreateDirectory()
        {

            if (Directory.Exists(Path))
                return;

            if (OperatingSystem.IsWindows())
                Directory.CreateDirectory(Path);

            else
                Directory.CreateDirectory(
                    Path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                );

        }

        #endregion

        #region Dispose()

        public void Dispose()
        {
            lock (updateLock)
            {

                foreach (var entry in entries.Values)
                    entry.Dispose();

                entries.Clear();
                publicKeys.Clear();

                served    = null;
                servedId  = null;

            }
        }

        #endregion

        #region (override) ToString()

        public override String ToString()
        {
            lock (updateLock)
                return $"{entries.Count} key(s) in '{Path}', {entries.Values.Count(entry => entry.Certificate is not null)} with a certificate";
        }

        #endregion

    }

}
