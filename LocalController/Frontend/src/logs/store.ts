import { api, type LogEntry } from '../api/client';

// The browser's copy of the local controller's log, fed by two things: a snapshot from
// the JSON API and the Server-Sent Events stream. Both carry ids from the same
// counter on the local controller, and the counter only ever grows - so an entry is
// taken only when it is newer than the snapshot, and a replayed stream (Hermod
// repeats its cached events to every new client) does no harm.
//
// The stream is not filtered: the snapshot and the events have to be about the
// same thing, or a filter changed at the browser would leave holes that only
// another round trip could fill. Filtering by tag and by level happens on this
// side, over what is already here, and is therefore instant.

export type StoreEvent =
    | { type: 'entries';  added: LogEntry[] }
    | { type: 'reloaded' }
    | { type: 'stream' }
    | { type: 'error';    text: string };

type Listener = (event: StoreEvent) => void;

/**
 * How many entries the page keeps. The local controller keeps its own number and
 * answers with at most that many; this is the ceiling for a page that has been
 * open for a week.
 */
const MAX_ENTRIES = 5_000;

/** How much of the log a fresh page loads before it starts following along. */
const SNAPSHOT_SIZE = 1_000;


export class LogStore {

    /** What is known, oldest first. */
    readonly entries: LogEntry[] = [];

    /** Every tag the local controller has seen, for the filter. */
    readonly tags = new Set<string>();

    /** Whether the event stream is up. */
    streamConnected = false;

    /** The newest id this page knows about. */
    lastId = 0;

    /** How many entries the local controller keeps. */
    capacity = 0;

    private source:              EventSource | null = null;
    private readonly listeners = new Set<Listener>();


    onChange(listener: Listener): () => void {
        this.listeners.add(listener);
        return () => this.listeners.delete(listener);
    }


    /** Open the stream; every 'open' - the first and every reconnect - reloads. */
    start(): void {

        if (this.source !== null)
            return;

        const source = new EventSource(api.eventsURL);
        this.source  = source;

        source.addEventListener('open', () => {
            this.streamConnected = true;
            this.emit({ type: 'stream' });
            void this.reload();
        });

        source.addEventListener('error', () => {
            if (this.streamConnected) {
                this.streamConnected = false;
                this.emit({ type: 'stream' });
            }
        });

        source.addEventListener('log', event => {

            try
            {
                this.apply([JSON.parse((event as MessageEvent<string>).data) as LogEntry]);
            }
            catch (error)
            {
                console.warn('A log event could not be read:', error);
            }

        });

    }

    /** Close the stream and forget everything, e.g. at sign-out. */
    stop(): void {

        this.source?.close();
        this.source = null;

        this.streamConnected = false;
        this.lastId          = 0;

        this.entries.length = 0;
        this.tags.clear();

    }

    /** Throw away what this page shows, without touching the local controller's log. */
    clear(): void {
        this.entries.length = 0;
        this.emit({ type: 'reloaded' });
    }


    /** The snapshot: what happened up to now, as far back as the local controller keeps. */
    async reload(): Promise<void> {

        try
        {

            const page = await api.logs(SNAPSHOT_SIZE);

            this.entries.length = 0;
            this.entries.push(...page.entries);

            this.lastId    = Math.max(page.lastId, ...page.entries.map(entry => entry.id), 0);
            this.capacity  = page.capacity;

            for (const tag of page.tags)
                this.tags.add(tag);

            for (const entry of page.entries)
                for (const tag of entry.tags)
                    this.tags.add(tag);

            this.emit({ type: 'reloaded' });

        }
        catch (error)
        {
            this.emit({
                type: 'error',
                text: `Could not load the log: ${error instanceof Error ? error.message : String(error)}`
            });
        }

    }


    /** Everything the stream delivered that the snapshot did not already hold. */
    private apply(incoming: LogEntry[]): void {

        const added = incoming.filter(entry => entry.id > this.lastId).
                               sort((a, b) => a.id - b.id);

        if (added.length === 0)
            return;

        for (const entry of added) {

            this.entries.push(entry);
            this.lastId = entry.id;

            for (const tag of entry.tags)
                this.tags.add(tag);

        }

        if (this.entries.length > MAX_ENTRIES)
            this.entries.splice(0, this.entries.length - MAX_ENTRIES);

        this.emit({ type: 'entries', added });

    }

    private emit(event: StoreEvent): void {

        for (const listener of this.listeners)
        {
            try
            {
                listener(event);
            }
            catch (error)
            {
                console.error('A log listener failed:', error);
            }
        }

    }

}


export const logs = new LogStore();
