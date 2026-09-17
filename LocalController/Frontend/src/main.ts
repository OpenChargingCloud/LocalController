import './styles/app.scss';

// FontAwesome: the CSS ends up in the extracted stylesheet, the referenced
// font files become hashed assets below /assets/.
import '@fortawesome/fontawesome-free/css/fontawesome.css';
import '@fortawesome/fontawesome-free/css/solid.css';

import { auth } from './auth';
import { html, must, render } from './html';
import { logs } from './logs/store';
import { Router } from './router';

import { configurationPage }      from './pages/configuration';
import { dnsPage }                from './pages/dns';
import { ntsPage }                from './pages/nts';
import { csmsPage }               from './pages/csms';
import { ocppServerPage }         from './pages/ocppServer';
import { stationLoginsPage }      from './pages/stationLogins';
import { serverCertificatesPage } from './pages/serverCertificates';
import { clientTrustPage }        from './pages/clientTrust';
import { loginPage }              from './pages/login';
import { logsPage }               from './pages/logs';
import { notFoundPage }           from './pages/notFound';


const root = document.getElementById('app');

if (root === null)
    throw new Error("The '#app' element is missing!");

render(root, html`<div id="page" class="page"></div>`);

const router = new Router({
    routes: [
        // "/" is the configuration, and is a route of its own rather than a
        // redirect to /configuration: the sign-in remembers where somebody was
        // going, and for the first visit that is "/" - which would otherwise be
        // a page that exists on the way in and not on the way back.
        { path: '/',                    page: configurationPage,  guard: auth.requireSignIn },
        { path: '/configuration',       page: configurationPage,  guard: auth.requireSignIn },
        { path: '/configuration/dns',   page: dnsPage,            guard: auth.requireSignIn },
        { path: '/configuration/nts',   page: ntsPage,            guard: auth.requireSignIn },

        { path: '/configuration/csms',                      page: csmsPage,               guard: auth.requireSignIn },
        { path: '/configuration/ocpp-server',               page: ocppServerPage,         guard: auth.requireSignIn },
        { path: '/configuration/ocpp-server/logins',        page: stationLoginsPage,      guard: auth.requireSignIn },
        { path: '/configuration/ocpp-server/certificates',  page: serverCertificatesPage, guard: auth.requireSignIn },
        { path: '/configuration/ocpp-server/trust',         page: clientTrustPage,        guard: auth.requireSignIn },

        { path: '/logs',                page: logsPage,           guard: auth.requireSignIn },
        { path: '/login',               page: loginPage }
    ],
    outlet:       must<HTMLElement>(root, '#page'),
    notFound:     notFoundPage,
    titleSuffix:  ' · Local Controller'
});

// Signed in: follow the controller's log from now on, whichever page is open -
// so that opening the Logs page shows what happened while somebody was
// reading the configuration, and not an empty list.
// Signed out - by the button, or because the session expired and a request
// came back with 401: close the stream, forget the log, show the sign-in.
auth.onChange(user => {

    if (user !== null) {
        logs.start();
        return;
    }

    logs.stop();

    if (location.pathname !== '/login')
        router.navigate(auth.requireSignIn(new URL(location.href)) ?? '/login', true);

});

// Find out who is signed in before the first page renders, so that a reload on
// a deep URL does not flash the sign-in page on its way back to where it was.
void (async () => {
    await auth.refresh();
    router.start();
})();
