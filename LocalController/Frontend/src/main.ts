import './styles/app.scss';

// FontAwesome: the CSS ends up in the extracted stylesheet, the referenced
// font files become hashed assets below /assets/.
import '@fortawesome/fontawesome-free/css/fontawesome.css';
import '@fortawesome/fontawesome-free/css/solid.css';

import { nodeMenu, startNode } from '@node/start';

import { configurationPage }      from './pages/configuration';
import { csmsPage }               from './pages/csms';
import { ocppServerPage }         from './pages/ocppServer';
import { stationLoginsPage }      from './pages/stationLogins';
import { serverCertificatesPage } from './pages/serverCertificates';
import { clientTrustPage }        from './pages/clientTrust';

// What a local controller has pages for beside what every node has: the line
// up to its CSMS, and the server its charging stations connect to. The sign-in,
// the log, the name servers, the time servers, the certificate store, the
// frame and following the log while somebody is signed in are every node's -
// see WWCP_Node's start.ts.
startNode({

    name:  'Local Controller',
    icon:  'fa-sitemap',

    menu: [
        nodeMenu.configuration([
            nodeMenu.dns,
            nodeMenu.nts,
            { path: '/configuration/csms',                      label: 'CSMS connection',     icon: 'fa-satellite-dish',    permission: [ 'csms:read' ]         },
            { ...nodeMenu.certificates,                         label: 'Certificate store',   icon: 'fa-vault'                                                  },
            { path: '/configuration/ocpp-server',               label: 'Charging stations',   icon: 'fa-charging-station',  permission: [ 'stations:read' ]     },
            { path: '/configuration/ocpp-server/logins',        label: 'Logins and groups',   icon: 'fa-users-gear',        permission: [ 'stations:read' ]     },
            { path: '/configuration/ocpp-server/certificates',  label: 'Server certificates', icon: 'fa-certificate',       permission: [ 'certificates:read' ] },
            { path: '/configuration/ocpp-server/trust',         label: 'Accepted chains',     icon: 'fa-user-shield',       permission: [ 'certificates:read' ] }
        ]),
        nodeMenu.logs
    ],

    // "Certificate store", because there is a page called "Server certificates"
    // as well - the charging station server's. And the certificate the line up
    // to the CSMS signs in with, which the controller does not let go of until
    // another one is chosen on the CSMS page.
    certificates: {
        title:   'Certificate store',
        chosen:  {
            csmsClientCertificate:  { label: 'signs in to the CSMS', title: 'Chosen on the CSMS page; deleted only once another one is' }
        }
    },

    // "/" is every node's: the first page of the menu the person signed in may
    // open - the configuration for whoever may read it, and the name servers,
    // say, for a desk that may read only those, where the configuration's own
    // page had answered 403 (found by the charging station).
    pages: {

        '/configuration':                           configurationPage,

        '/configuration/csms':                      csmsPage,
        '/configuration/ocpp-server':               ocppServerPage,
        '/configuration/ocpp-server/logins':        stationLoginsPage,
        '/configuration/ocpp-server/certificates':  serverCertificatesPage,
        '/configuration/ocpp-server/trust':         clientTrustPage

    }

});
