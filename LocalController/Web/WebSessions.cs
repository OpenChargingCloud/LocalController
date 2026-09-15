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

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;

#endregion

namespace cloud.charging.open.LocalController.Web
{

    /// <summary>
    /// Who may use the web interface: one username, one password, and a session
    /// cookie for every browser that got both right.
    /// </summary>
    /// <remarks>
    /// The sessions themselves are Hermod's <see cref="SessionStore"/> - random
    /// tokens, a sliding idle timeout and a maximum lifetime. What is left here
    /// is the part a local controller has of its own: one login rather than an
    /// account database, and the cookie the token travels in.
    ///
    /// The login is not fixed for the lifetime of the process either:
    /// <see cref="UpdateLogin"/> puts another one in force and ends every other
    /// session when it does - or a password change would not do what whoever
    /// changed it thinks it does.
    /// </remarks>
    public sealed class WebSessions
    {

        #region Data

        /// <summary>
        /// The default name of the session cookie.
        /// </summary>
        public static readonly HTTPCookieName  DefaultCookieName       = HTTPCookieName.Parse("LocalController");

        /// <summary>
        /// The default idle timeout: a session ends when it was not used for this long.
        /// </summary>
        public static readonly TimeSpan        DefaultIdleTimeout      = TimeSpan.FromHours(12);

        /// <summary>
        /// The default maximum lifetime of a session.
        /// </summary>
        public static readonly TimeSpan        DefaultMaximumLifetime  = TimeSpan.FromDays(7);

        #endregion

        #region Properties

        /// <summary>
        /// The login in force: the one username and the hash of its password.
        /// </summary>
        public WebLoginSettings  Login             { get; private set; }

        /// <summary>
        /// The one username.
        /// </summary>
        public String            Username
            => Login.Username;

        /// <summary>
        /// The one username, as the session store knows it.
        /// </summary>
        public User_Id           UserId
            => User_Id.Parse(Login.Username);

        /// <summary>
        /// What the login in force signs in as.
        /// </summary>
        public IReadOnlyList<UserRole>  Roles
            => Login.Roles;

        /// <summary>
        /// What those roles grant together.
        /// </summary>
        public Permissions       Permissions
            => Login.Permissions;

        /// <summary>
        /// What the browser behind this session may do.
        /// </summary>
        /// <remarks>
        /// Asked about the session rather than about the local controller, although
        /// there is one login and the answer cannot yet differ: the day this
        /// file holds more than one login, every caller is already asking the
        /// question whose answer changes.
        /// </remarks>
        public Permissions PermissionsOf(Session Session)
            => Login.Permissions;

        /// <summary>
        /// The live sessions.
        /// </summary>
        public SessionStore      Store             { get; }

        /// <summary>
        /// The name of the session cookie.
        /// </summary>
        public HTTPCookieName    CookieName        { get; }

        /// <summary>
        /// Whether the cookie is marked "secure", i.e. only ever sent over TLS.
        /// </summary>
        public Boolean           SecureCookies     { get; }

        /// <summary>
        /// A session ends when it was not used for this long.
        /// </summary>
        public TimeSpan          IdleTimeout       { get; }

        /// <summary>
        /// A session ends this long after the sign-in at the latest.
        /// </summary>
        public TimeSpan          MaximumLifetime   { get; }

        /// <summary>
        /// Where the sessions read the time: when one was created, when it was
        /// last seen, and whether it has expired.
        /// </summary>
        public TimeProvider      TimeProvider      { get; }

        /// <summary>
        /// How many sessions are live right now.
        /// </summary>
        public Int32             Count
            => Store.Count;

        #endregion

        #region Constructor(s)

        /// <summary>
        /// Create the sessions of the one user.
        /// </summary>
        /// <param name="Login">The login: the username and the hash of its password.</param>
        /// <param name="SecureCookies">Whether the cookie is only ever sent over TLS; true when the server speaks TLS.</param>
        /// <param name="CookieName">The name of the session cookie.</param>
        /// <param name="IdleTimeout">A session ends when it was not used for this long; 12 hours by default.</param>
        /// <param name="MaximumLifetime">A session ends this long after the sign-in at the latest; 7 days by default.</param>
        /// <param name="TimeProvider">Where the sessions read the time; the system clock by default.</param>
        public WebSessions(WebLoginSettings  Login,
                           Boolean           SecureCookies     = false,
                           HTTPCookieName?   CookieName        = null,
                           TimeSpan?         IdleTimeout       = null,
                           TimeSpan?         MaximumLifetime   = null,
                           TimeProvider?     TimeProvider      = null)
        {

            this.Login            = Login ?? throw new ArgumentNullException(nameof(Login));
            this.SecureCookies    = SecureCookies;
            this.CookieName       = CookieName      ?? DefaultCookieName;
            this.IdleTimeout      = IdleTimeout     ?? DefaultIdleTimeout;
            this.MaximumLifetime  = MaximumLifetime ?? DefaultMaximumLifetime;
            this.TimeProvider     = TimeProvider    ?? System.TimeProvider.System;

            this.Store            = new SessionStore(
                                        IdleTimeout:      this.IdleTimeout,
                                        MaximumLifetime:  this.MaximumLifetime,
                                        TimeProvider:     this.TimeProvider
                                    );

        }

        #endregion


        #region TryLogin(Username, Password, out Session)

        /// <summary>
        /// Start a session when username and password are right.
        /// </summary>
        /// <param name="Username">What was typed as the username.</param>
        /// <param name="Password">What was typed as the password.</param>
        /// <param name="Session">The new session.</param>
        public Boolean TryLogin(String?                           Username,
                                String?                           Password,
                                [NotNullWhen(true)] out Session?  Session)
        {

            Session = null;

            if (!Login.Verify(Username, Password))
                return false;

            Session = Store.Create(UserId);
            return true;

        }

        #endregion

        #region UpdateLogin(NewLogin, ExceptToken = null)

        /// <summary>
        /// Put another login in force and end every session but the one that
        /// made the change.
        /// </summary>
        /// <remarks>
        /// Changing the password has to end the other sessions, or it does not
        /// do what whoever changed it thinks it does: a browser that was signed
        /// in with the old password would keep the page for as long as its
        /// cookie lives. The session doing the change is kept, so that whoever
        /// changed it does not sign themselves out.
        /// </remarks>
        /// <param name="NewLogin">The login from now on.</param>
        /// <param name="ExceptToken">A session to keep, usually the one asking.</param>
        /// <returns>The number of sessions ended.</returns>
        public Int32 UpdateLogin(WebLoginSettings   NewLogin,
                                 SecurityToken_Id?  ExceptToken   = null)
        {

            var previous = UserId;

            Login = NewLogin ?? throw new ArgumentNullException(nameof(NewLogin));

            return Store.RemoveAllForUser(previous, ExceptToken);

        }

        #endregion

        #region TryGetSession(Request, out Session)

        /// <summary>
        /// The live session behind the request's cookie, if there is one.
        /// </summary>
        /// <remarks>
        /// Finding one is also using it: the idle timeout counts from the last
        /// request, not from the sign-in.
        /// </remarks>
        public Boolean TryGetSession(HTTPRequest                       Request,
                                     [NotNullWhen(true)] out Session?  Session)
        {

            Session = null;

            return TryGetToken(Request, out var token) &&
                   Store.TryGet(token, out Session);

        }

        #endregion

        #region HasCookie(Request)

        /// <summary>
        /// Whether the request carries a session cookie at all - live or stale.
        /// </summary>
        public Boolean HasCookie(HTTPRequest Request)
            => Request.Cookies?.Contains(CookieName) == true;

        #endregion

        #region SignOut(Request)

        /// <summary>
        /// End the session behind the request's cookie.
        /// </summary>
        /// <returns>Whether there was a live session to end.</returns>
        public Boolean SignOut(HTTPRequest Request)

            => TryGetToken(Request, out var token) &&
               Store.Remove(token);

        #endregion


        #region SessionCookie(Session) / ExpiredCookie()

        // One HTTPCookie, parsed as one: HTTPCookies.Parse(String) is made for
        // the Cookie header of a request, where a semicolon separates cookies,
        // and would turn "Path=/" and "HttpOnly" into cookies of their own.

        /// <summary>
        /// The Set-Cookie of a sign-in: the token, HttpOnly, for this site only.
        /// </summary>
        public HTTPCookies SessionCookie(Session Session)

            => new (HTTPCookie.Parse(
                        String.Concat(CookieName, "=", Session.Token, CookieSettings(Session.ExpiresAt))
                    ));

        /// <summary>
        /// The Set-Cookie of a sign-out: the same cookie, expired in 1970, so
        /// that the browser drops it.
        /// </summary>
        public HTTPCookies ExpiredCookie()

            => new (HTTPCookie.Parse(
                        String.Concat(CookieName, "=", CookieSettings(DateTimeOffset.UnixEpoch))
                    ));

        #endregion


        #region (private) TryGetToken(Request, out Token)

        private Boolean TryGetToken(HTTPRequest           Request,
                                    out SecurityToken_Id  Token)
        {

            Token = default;

            return Request.Cookies is not null &&
                   Request.Cookies.TryGet(CookieName, out var cookie) &&
                   cookie?.Value is String value &&
                   SecurityToken_Id.TryParse(value, out Token);

        }

        #endregion

        #region (private) CookieSettings(Expires)

        private String CookieSettings(DateTimeOffset Expires)

            => String.Concat("; Expires=", Expires.ToRFC1123(),
                             "; Path=/",
                             "; SameSite=strict",
                             SecureCookies ? "; secure" : "",
                             "; HttpOnly");

        #endregion

    }

}
