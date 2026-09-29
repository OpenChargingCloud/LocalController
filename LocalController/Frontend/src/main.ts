import './styles/app.scss';

// FontAwesome: the CSS ends up in the extracted stylesheet, the referenced
// font files become hashed assets below /assets/.
import '@fortawesome/fontawesome-free/css/fontawesome.css';
import '@fortawesome/fontawesome-free/css/solid.css';

import { nodeMenu, startNode } from '@node/start';

import { configurationPage }      from './pages/configuration';
import { dnsPage }                from './pages/dns';
import { ntsPage }                from './pages/nts';
import { csmsPage }               from './pages/csms';
import { certificateStorePage }   from './pages/certificateStore';
import { ocppServerPage }         from './pages/ocppServer';
import { stationLoginsPage }      from './pages/stationLogins';
import { serverCertificatesPage } from './pages/serverCertificates';
import { clientTrustPage }        from './pages/clientTrust';

// What a local controller has pages for beside what every node has: the line
// up to its CSMS, and the server its charging stations connect to. The sign-in,
// the log, the frame and following the log while somebody is signed in are
// every node's - see WWCP_Node's start.ts.
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

    pages: {

        // "/" is the configuration, and is a page of its own rather than a
        // redirect to /configuration: the sign-in remembers where somebody was
        // going, and for the first visit that is "/".
        '/':                                        configurationPage,
        '/configuration':                           configurationPage,
        '/configuration/dns':                       dnsPage,
        '/configuration/nts':                       ntsPage,

        '/configuration/csms':                      csmsPage,
        '/configuration/certificates':              certificateStorePage,
        '/configuration/ocpp-server':               ocppServerPage,
        '/configuration/ocpp-server/logins':        stationLoginsPage,
        '/configuration/ocpp-server/certificates':  serverCertificatesPage,
        '/configuration/ocpp-server/trust':         clientTrustPage

    }

});
