import { nodeAPI,
         request,
         type Certificate        as NodeCertificate,
         type CertificateImport  as NodeCertificateImport,
         type CertificateStore   as NodeCertificateStore,
         type ChosenCertificate,
         type NodeConfiguration,
         type NodeMe,
         type NodeResource,
         type NodeStatus,
         type Operation }  from '@node/api/client';
import { connectionsAPI }    from '@ocpp/api/connections';
import { stationServerAPI }  from '@ocpp/api/stationServer';


// What every node answers - the log, name resolution, the time, the store,
// who is signed in - and how it is asked are WWCP_Node's, and every page here
// reads them from this module as before. What follows is what a local
// controller adds: its resources, what its status and its configuration say
// beyond every node's, the kinds its store keeps, and its own routes.
export * from '@node/api/client';

// What a node that dials its back ends keeps - the lines up to the CSMS as
// connections, and what they sign in with - and how it is
// asked are the same on a charging station, and are WWCP_OCPP_Node's: named
// here as well. What the charging station server keeps - its section, its
// certificates, the chains it accepts and the stations that may sign in - is
// WWCP_OCPP_Node's too, in '@ocpp/api/stationServer', and named there only:
// two of its names, StationLogin and KeyAlgorithm, are names of something
// else in the modules above.
export * from '@ocpp/api/connections';


/**
 * What a role may be allowed to touch on this local controller: what every
 * node has, and what a local controller adds to it. The log and the six of
 * signed OCPP messages are every node's as well (WWCP_Node's NodeResources),
 * and named here until WWCP_Node's NodeResource names them.
 */
export type Resource = NodeResource
                     | 'log' | 'node' | 'reset' | 'reset-immediate' | 'state' | 'firmware' | 'signatures'
                     | 'csms' | 'stations' | 'openadr';

/** What somebody signed in to this local controller may do: an operation on a resource, written "dns:edit". */
export type Permission = `${Resource}:${Operation}`;

/** Who is signed in to the web interface. */
export type Me = NodeMe<Resource>;

/** How the local controller is doing right now: every node's, and its OCPP identity. */
export interface Status extends NodeStatus {
    ocppId:  string;
}

/**
 * What the local controller is made of: every node's sections, and its own.
 * Only the shape the Configuration page relies on is named; the fields of
 * each section are rendered from whatever the controller sends, and which
 * sections there are is the page's to say.
 */
export interface Configuration extends NodeConfiguration {
    controller:  Record<string, unknown>;
    ocpp:        Record<string, unknown>;
}

/**
 * What a certificate in this local controller's store is for. A TLS root is
 * believed of a server it connects to, an identity is what it presents in TLS
 * itself - a server identity as its web interface -, and a server certificate
 * is neither, but kept to recognise a server by its fingerprint; an issuing CA
 * is neither either, but kept to build the chain of a certificate shown
 * without it.
 */
export type CertificateKind = 'tlsRoot' | 'tlsServer' | 'tlsIdentity' | 'tlsServerIdentity' | 'tlsIntermediate';

/** One certificate in the store. */
export type Certificate = NodeCertificate<CertificateKind>;

/** What an import sends. */
export type CertificateImport = NodeCertificateImport<CertificateKind>;

/** The whole store, and which of it the CSMS connection signs in with. */
export interface CertificateStore extends NodeCertificateStore<CertificateKind> {
    /** Which handle the CSMS connection signs in with under security profile 3, and as which kind. */
    chosen?: {
        csmsClientCertificate:  ChosenCertificate | null;
    };
}


/**
 * The charging station management system above this local controller: whether
 * it reports to one at all, and how every line up to it is kept. The lines
 * themselves - where each goes, what it signs in with and how it knows its
 * server - are connections, as a charging station's are: StationConnections.
 */
export interface CSMSConfiguration {
    enabled:                     boolean;
    nextHopNodeId?:              string;
    subprotocols:                string[];
    /** The oldest version of TLS a line is made over, where the controller says it. */
    minimumTLSVersion?:          string;
    checkCertificateRevocation:  boolean;
    pingEvery:                   number;
    requestTimeout:              number;
    reconnectInitialDelay:       number;
    reconnectMaxDelay:           number;
    /**
     * The TLS identity of the certificate store a line over TLS holds up where
     * its connection names no client key of its own, by its handle; empty for none.
     */
    clientCertificate?:          string;
    /** That identity as the store has it - or that it has none by that handle. */
    clientCertificateIs?:        { id: string; missing: boolean; label?: string; subject?: string; notAfter?: string; usable?: boolean };
    /**
     * How the messages on a line say where they go and where they come from:
     * "overlay", in the frame of the overlay network, or "standard", in the
     * plain OCPP frame.
     */
    networkingMode?:             string;
    state: {
        connected:           boolean;
        connectedSince:      string | null;
        lastProblem:         string | null;
        /** How many lines up to the CSMS are written down, as connections. */
        connections:         number;
        waitingForARestart:  string[];
    };
}

/**
 * What the CSMS section may be changed to. Where a line goes, the version of
 * HTTP it is opened over and its security profile are a connection's, and
 * refused here.
 */
export interface CSMSUpdate {
    enabled?:                     boolean;
    subprotocols?:                string[];
    checkCertificateRevocation?:  boolean;
    pingEvery?:                   number;
    requestTimeout?:              number;
    reconnectInitialDelay?:       number;
    reconnectMaxDelay?:           number;
    /** The TLS identity a line holds up where its connection names none; null for none, and left out to leave it alone. */
    clientCertificate?:           string | null;
}


/**
 * What is going on, apart from how the local controller is configured: what
 * "state:read" shows. Nothing of it says an address, a login or a secret.
 */
export interface ControllerState {
    csms:       { enabled: boolean; connected: boolean; connectedSince: string | null };
    stations:   { connected: number;
                  seen: { id: string; connected: boolean; over: 'WebSocket' | 'SSH' | null;
                          connectedSince: string | null; lastSeen: string }[] };
    openADR:    { running: boolean; signedIn: boolean; lastPoll: string | null; lastRead: string | null;
                  importKW: number | null; exportKW: number | null;
                  limits: { eventID: string; name: string | null; kind: string; kW: number;
                            start: string; end: string | null; state: string }[] };
    timestamp:  string;
}

/** The routes every node has, typed with what a local controller says its own of them are. */
const node = nodeAPI<{ me: Me; status: Status; configuration: Configuration; kind: CertificateKind; store: CertificateStore }>();

export const api = {

    ...node,

    /** What is going on: the line up to the CSMS, the stations seen, the limits in force. */
    state: () => request<ControllerState>('GET', '/state'),

    /**
     * Whether this controller reports to a CSMS, and how every line up to it
     * is kept. The lines themselves are the connections below.
     */
    csms: {

        get:          ()  => request<CSMSConfiguration>('GET', '/configuration/csms'),

        save:         (update: CSMSUpdate) =>
                          request<CSMSConfiguration>('PUT', '/configuration/csms', update)

    },

    // The lines up to the CSMS and what they sign in with - api.connections
    // and api.authentications - as every node that dials has them.
    ...connectionsAPI,

    // The charging station server, its certificates, the chains it accepts
    // and the stations that may sign in - api.ocppServer - as every node that
    // serves those below it has them.
    ...stationServerAPI

};
