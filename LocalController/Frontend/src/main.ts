import './styles/app.scss';

// FontAwesome: the CSS ends up in the extracted stylesheet, the referenced
// font files become hashed assets below /assets/.
import '@fortawesome/fontawesome-free/css/fontawesome.css';
import '@fortawesome/fontawesome-free/css/solid.css';

import { movedTo, nodeMenu, startNode } from '@node/start';

import { configurationPage }      from './pages/configuration';
import { csmsPage }               from './pages/csms';
import { ocppServerPage }         from '@ocpp/pages/ocppServer';
import { stationLoginsPage }      from '@ocpp/pages/stationLogins';
import { clientTrustPage }        from '@ocpp/pages/clientTrust';
import { statePage }              from './pages/state';

import { sshKeysSection }         from '@ocpp/sshKeys';
import { connectionsPage }        from '@ocpp/pages/connections';
import { ocppLogMenuEntry, ocppLogPage } from '@ocpp/pages/ocppLog';
import { commissioningPage }      from '@ocpp/pages/commissioning';
import { setConnectionsResource, setNodeNoun, setNodeTraits } from '@ocpp/settings';

// The lines up to the CSMS are connections, drawn by the pages a charging
// station draws its own with. Here whoever may read or change the CSMS
// section may read or change them; the pages speak of a local controller,
// which is one OCPP node whatever it speaks upwards, and is itself connected to,
// by its charging stations.
setConnectionsResource('csms');
setNodeNoun('local controller');
setNodeTraits({ oneNodePerOCPPVersion: false, isConnectedTo: true });

// What a local controller has pages for beside what every node has: whether
// it reports to a CSMS, the lines up to it with what they sign in with,
// the server its charging stations connect to, and what is
// going on with both - the State, for whoever may see it and no more (the
// support, say, whose first page it is). The sign-in,
// the log, the name servers, the time servers, the certificate store, the
// frame and following the log while somebody is signed in are every node's -
// see WWCP_Node's start.ts. Server identities is the node's store too: who the
// web interface is where it is served over HTTPS from the store, and what the
// station port presents - a TLS server identity told "ocpp", or the carrier
// of a server key the stations pin, with the key pins and the switch to the
// next key.
startNode({

    name:  'Local Controller',
    icon:  'fa-sitemap',

    menu: [
        nodeMenu.configuration([
            nodeMenu.dns,
            nodeMenu.nts,
            nodeMenu.ssh,
            { path: '/configuration/csms',                      label: 'CSMS connection',     icon: 'fa-satellite-dish',    permission: [ 'csms:read' ]         },
            // The lines up to the CSMS and, on a tab of the same page, what
            // they sign in with: read by whoever may read the CSMS section.
            // The keys they hold up have no page: they are TLS identities of
            // the certificate store, made, waiting for their certificate and
            // kept on Client identities - where the SSH keys are as well.
            { path: '/configuration/connections',               label: 'Connections',         icon: 'fa-network-wired',     permission: [ 'csms:read' ]         },
            { ...nodeMenu.certificates,                         label: 'Certificate store',   icon: 'fa-vault'                                                  },
            nodeMenu.identities,
            nodeMenu.serverIdentities,
            { path: '/configuration/ocpp-server',              label: 'Charging stations',   icon: 'fa-charging-station',  permission: [ 'stations:read' ]     },
            { path: '/configuration/ocpp-server/logins',        label: 'WebSocket logins',    icon: 'fa-users-gear',        permission: [ 'stations:read' ]     },
            { path: '/configuration/ocpp-server/trust',         label: 'Accepted chains',     icon: 'fa-user-shield',       permission: [ 'certificates:read' ] },
            { path: '/configuration/commissioning',             label: 'Commissioning',       icon: 'fa-screwdriver-wrench', permission: [ 'commissioning:read' ] }
        ]),
        { path: '/state', label: 'State', icon: 'fa-heart-pulse', permission: [ 'state:read' ] },
        // Every OCPP message this node sent and received.
        ocppLogMenuEntry,
        nodeMenu.logs
    ],

    // "Certificate store", as it has been called here since the charging
    // station server had a page of certificates of its own. And the certificate the line up
    // to the CSMS signs in with, which the controller does not let go of until
    // another one is chosen on the CSMS page: a TLS identity, marked on the
    // Client identities page, where who the controller is as a client is kept.
    certificates: {
        title:   'Certificate store',
        chosen:  {
            csmsClientCertificate:  { label: 'signs in to the CSMS', title: 'Chosen on the CSMS page; deleted only once another one is' }
        },

        // And who it is where it connects over ssh://: the keys it signs in
        // with, which are no certificates and stand among what it presents
        // all the same.
        sections: context => [ sshKeysSection(context) ],

        // The station port's server keys, on Server identities: offered there
        // before it has one.
        serverKeyListeners: [ 'ocpp' ]

    },

    // "/" is every node's: the first page of the menu the person signed in may
    // open - the configuration for whoever may read it, and the name servers,
    // say, for a desk that may read only those, where the configuration's own
    // page had answered 403 (found by the charging station).
    pages: {

        '/ocpp-log':                                ocppLogPage,

        '/configuration':                           configurationPage,

        '/configuration/csms':                      csmsPage,
        '/configuration/connections':               connectionsPage,
        // Where the credentials were while they had a page of their own:
        // they are a tab of the connections, which is where they are used.
        '/configuration/authentication':            movedTo('/configuration/connections?tab=credentials', 'Connections'),
        // Where the keys were while they had a page of their own.
        '/configuration/client-keys':               movedTo('/configuration/client-identities', 'Client identities'),
        '/configuration/ocpp-server':               ocppServerPage,
        '/configuration/ocpp-server/logins':        stationLoginsPage,
        // Where what the station port presents was while it had a store and
        // a page of its own.
        '/configuration/ocpp-server/certificates':  movedTo('/configuration/server-identities', 'Server identities'),
        '/configuration/ocpp-server/trust':         clientTrustPage,
        '/configuration/commissioning':             commissioningPage,

        '/state':                                   statePage

    }

});
