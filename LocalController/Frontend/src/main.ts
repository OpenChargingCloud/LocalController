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
import { statePage }              from './pages/state';

import { authenticationPage }     from '@ocpp/pages/authentication';
import { connectionsPage }        from '@ocpp/pages/connections';
import { clientKeysPage }         from '@ocpp/pages/clientKeys';
import { setConnectionsResource, setNodeNoun, setNodeTraits } from '@ocpp/settings';

// The lines up to the CSMS are connections, drawn by the pages a charging
// station draws its own with. Here whoever may read or change the CSMS
// section may read or change them; the pages speak of a local controller,
// which is one OCPP node whatever it speaks upwards, and is dialled itself,
// by its charging stations.
setConnectionsResource('csms');
setNodeNoun('local controller');
setNodeTraits({ oneNodePerOCPPVersion: false, isDialled: true });

// What a local controller has pages for beside what every node has: whether
// it reports to a CSMS, the lines up to it with what they sign in with and
// their client keys, the server its charging stations connect to, and what is
// going on with both - the State, for whoever may see it and no more (the
// support, say, whose first page it is). The sign-in,
// the log, the name servers, the time servers, the certificate store, the
// frame and following the log while somebody is signed in are every node's -
// see WWCP_Node's start.ts. Server identities is the node's store too: who the
// web interface is where it is served over HTTPS from the store, in the menu
// only then. What the station port presents is under Server certificates, a
// store of its own.
startNode({

    name:  'Local Controller',
    icon:  'fa-sitemap',

    menu: [
        nodeMenu.configuration([
            nodeMenu.dns,
            nodeMenu.nts,
            nodeMenu.ssh,
            { path: '/configuration/csms',                      label: 'CSMS connection',     icon: 'fa-satellite-dish',    permission: [ 'csms:read' ]         },
            // The lines up to the CSMS, what they sign in with and the keys
            // they hold up: read by whoever may read the CSMS section.
            { path: '/configuration/connections',               label: 'Connections',         icon: 'fa-network-wired',     permission: [ 'csms:read' ]         },
            { path: '/configuration/authentication',            label: 'Authentication',      icon: 'fa-user-lock',         permission: [ 'csms:read' ]         },
            { path: '/configuration/client-keys',               label: 'Client keys',         icon: 'fa-key',               permission: [ 'csms:read' ]         },
            { ...nodeMenu.certificates,                         label: 'Certificate store',   icon: 'fa-vault'                                                  },
            nodeMenu.identities,
            nodeMenu.serverIdentities,
            { path: '/configuration/ocpp-server',              label: 'Charging stations',   icon: 'fa-charging-station',  permission: [ 'stations:read' ]     },
            { path: '/configuration/ocpp-server/logins',        label: 'Logins and groups',   icon: 'fa-users-gear',        permission: [ 'stations:read' ]     },
            { path: '/configuration/ocpp-server/certificates',  label: 'Server certificates', icon: 'fa-certificate',       permission: [ 'certificates:read' ] },
            { path: '/configuration/ocpp-server/trust',         label: 'Accepted chains',     icon: 'fa-user-shield',       permission: [ 'certificates:read' ] }
        ]),
        { path: '/state', label: 'State', icon: 'fa-heart-pulse', permission: [ 'state:read' ] },
        nodeMenu.logs
    ],

    // "Certificate store", because there is a page called "Server certificates"
    // as well - the charging station server's. And the certificate the line up
    // to the CSMS signs in with, which the controller does not let go of until
    // another one is chosen on the CSMS page: a TLS identity, marked on the
    // Identities page, where who the controller is as a client is kept.
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
        '/configuration/connections':               connectionsPage,
        '/configuration/authentication':            authenticationPage,
        '/configuration/client-keys':               clientKeysPage,
        '/configuration/ocpp-server':               ocppServerPage,
        '/configuration/ocpp-server/logins':        stationLoginsPage,
        '/configuration/ocpp-server/certificates':  serverCertificatesPage,
        '/configuration/ocpp-server/trust':         clientTrustPage,

        '/state':                                   statePage

    }

});
