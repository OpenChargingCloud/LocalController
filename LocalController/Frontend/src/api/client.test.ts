/**
 * What the web interface does when the local controller does not answer.
 *
 * Run with `npm test`, which is Node's own runner reading the TypeScript as it
 * stands - no bundler, no browser, no dependency that is not already here.
 *
 * What is pinned is what was measured to be wrong: a page that waited 98
 * seconds after Save with both buttons greyed out and nothing to say, nine
 * requests left hanging by seven clicks, and a local controller that could not be
 * reached described to the operator in the browser's words rather than in
 * words about a local controller.
 */

import { strict as assert }       from 'node:assert';
import { registerHooks }          from 'node:module';
import { describe, it }           from 'node:test';

// The pages are written for webpack, which does not want the extension in a
// relative import; Node does. One hook puts it back for whatever this test
// loads, so that the client can be read exactly as the browser gets it.
registerHooks({
    resolve(specifier, context, next) {
        return specifier.startsWith('.') && !specifier.endsWith('.ts')
                   ? next(`${specifier}.ts`, context)
                   : next(specifier, context);
    }
});

// The client reads config.ts, which reads <meta> tags when it is loaded. There
// is no document here, so it is given the smallest one that answers: none of
// the tags are there, and the client falls back to its own defaults.
(globalThis as unknown as { document: unknown }).document = {
    querySelector: () => null
};

const { ApiError, NoAnswer, actWithin, afterAsking, answerWithin, api, request } =
    await import('./client.ts');


/** What fetch was called with, so that a test can look at the signal. */
let asked: { url: string; init: RequestInit }[] = [];

/** Put a fetch in front of the client that behaves as the test wants. */
function fetchThat(How: (Signal: AbortSignal) => Promise<Response>): void {

    asked = [];

    (globalThis as unknown as { fetch: unknown }).fetch =
        (url: string, init: RequestInit) => {
            asked.push({ url, init });
            return How(init.signal as AbortSignal);
        };

}

/** A local controller that takes the request and then says nothing, ever. */
const staysSilent = (Signal: AbortSignal): Promise<Response> =>
    new Promise((_, reject) => {
        Signal.addEventListener('abort', () => reject(new Error('aborted')));
    });

/** One that sends its headers and then stops halfway through the body. */
const stopsMidAnswer = (Signal: AbortSignal): Promise<Response> =>
    Promise.resolve({
        ok:      true,
        status:  200,
        text:    () => new Promise<string>((_, reject) => {
                     Signal.addEventListener('abort', () => reject(new Error('aborted')));
                 })
    } as unknown as Response);

/** One that answers at once, with whatever it was told to. */
const answers = (Status: number, Body: unknown) => (): Promise<Response> =>
    Promise.resolve({
        ok:          Status >= 200 && Status < 300,
        status:      Status,
        statusText:  '',
        text:        () => Promise.resolve(JSON.stringify(Body))
    } as unknown as Response);


describe('a local controller that does not answer', () => {

    it('is given up on, rather than waited for as long as the browser is willing to', async () => {

        fetchThat(staysSilent);

        const started  = Date.now();
        const problem  = await request('GET', '/status', undefined, 60).then(
                                   () => null,
                                   (error: unknown) => error
                               );

        assert.ok(problem instanceof NoAnswer,
                  `a silent local controller produced ${problem} rather than a NoAnswer`);
        assert.equal((problem as InstanceType<typeof NoAnswer>).reason, 'ran out of time');
        assert.ok(Date.now() - started < 2_000,
                  'the deadline did not fire');

    });

    it('is given up on even when it is silent halfway through the body', async () => {

        // A local controller that sends its headers and then stops hangs exactly as
        // thoroughly as one that never starts, and a deadline that covers only
        // the connection would not notice.
        fetchThat(stopsMidAnswer);

        const problem = await request('GET', '/status', undefined, 60).
                                  then(() => null, (error: unknown) => error);

        assert.ok(problem instanceof NoAnswer);
        assert.equal((problem as InstanceType<typeof NoAnswer>).reason, 'ran out of time',
                     'the body was not covered by the deadline');

    });

    it('is described in words about a local controller, not the browser\'s', async () => {

        // What the browser says here is "Failed to fetch", which names neither
        // the local controller nor anything to do about it.
        fetchThat(() => Promise.reject(new TypeError('Failed to fetch')));

        const problem = await request('GET', '/status').then(() => null, (error: unknown) => error);

        assert.ok(problem instanceof NoAnswer);
        assert.equal((problem as InstanceType<typeof NoAnswer>).reason, 'could not be reached');
        assert.ok(!(problem as Error).message.includes('fetch'),
                  `the operator was shown: ${(problem as Error).message}`);
        assert.match((problem as Error).message, /local controller/);

    });

});


describe('what somebody is told when it runs out', () => {

    it('does not claim a write was not carried out, because nobody knows that', async () => {

        // The page stopped waiting; the local controller may well have written the file
        // and been slow to say so. Telling somebody it did not work is how a
        // thing gets done twice.
        fetchThat(staysSilent);

        const problem = await request('PUT', '/configuration/power', {}, 60).
                                  then(() => null, (error: unknown) => error as Error);

        assert.match(problem!.message, /may still have carried this out/);
        assert.match(problem!.message, /reload/i);

    });

    it('does say so for a read, which changed nothing', async () => {

        fetchThat(staysSilent);

        const problem = await request('GET', '/configuration/power', undefined, 60).
                                  then(() => null, (error: unknown) => error as Error);

        assert.doesNotMatch(problem!.message, /carried this out/);
        assert.match(problem!.message, /did not answer within/);

    });

    it('counts the seconds it actually waited', async () => {

        fetchThat(staysSilent);

        const problem = await request('GET', '/status', undefined, 1_500).
                                  then(() => null, (error: unknown) => error as Error);

        assert.match(problem!.message, /within 2 seconds/);

    });

});


describe('a local controller that does answer', () => {

    it('is not given up on afterwards', async () => {

        // The timer has to be cleared, or a page left open long enough would
        // abort a connection the browser is quietly holding for the next
        // request. The signal says whether it was.
        fetchThat(answers(200, { version: '0.1.0' }));

        const answer = await request<{ version: string }>('GET', '/status', undefined, 50);

        assert.equal(answer.version, '0.1.0');

        await new Promise(resolve => setTimeout(resolve, 150));

        assert.equal(asked[0]!.init.signal!.aborted, false,
                     'the request was aborted after it had already been answered');

    });

    it('and says no is still an ApiError, with the local controller\'s own sentence', async () => {

        fetchThat(answers(400, { error: "'display' needs both 'dimFrom' and 'dimUntil', or neither." }));

        const problem = await request('PUT', '/configuration/display', {}).
                                  then(() => null, (error: unknown) => error);

        assert.ok(problem instanceof ApiError, 'a refusal was turned into something else');
        assert.ok(!(problem instanceof NoAnswer));
        assert.match((problem as Error).message, /dimFrom/);

    });

});


describe('how long the page is willing to wait', () => {

    it('allows far more than the local controller has ever needed', () => {

        // Every read and write of this local controller's own configuration measured
        // between 1 and 7 milliseconds. The deadline is here to notice silence
        // and not slowness, so it may be generous by orders of magnitude.
        assert.ok(answerWithin >= 10_000);
        assert.ok(actWithin > answerWithin, 'a write is not given longer than a read');

    });

    it('waits out the local controller\'s own patience when it has to ask somebody else', () => {

        // Measured on the vehicle: two name servers at three seconds each took
        // 6.2 seconds. A page that gave up at four would be reporting its own
        // impatience as the local controller's silence.
        assert.ok(afterAsking([3, 3]) > 6_200);

        // And one that is asked nothing waits the ordinary time.
        assert.equal(afterAsking([]), answerWithin);

    });

    it('grows with each name server it is given', () => {

        assert.ok(afterAsking([10, 10, 10]) > afterAsking([10, 10]));
        assert.equal(afterAsking([10, 10, 10]) - afterAsking([10, 10]), 10_000);

    });

});


describe('signing in', () => {

    // The sign-in is the one call that does not go to this local controller's own API.
    // Hermod's HTTPExt API is the only place that can check a password, so the
    // frontend posts there and asks "me" afterwards for what that account may
    // do - and a test is the only thing that notices if it ever posts the
    // password to the wrong place.

    it('posts the password to the HTTPExt API and nowhere else', async () => {

        fetchThat(() => Promise.resolve(
            new Response(JSON.stringify({ username: 'root', roles: [], permissions: [] }),
                         { status: 200, headers: { 'Content-Type': 'application/json' } })
        ));

        await api.auth.login('root', 'hunter2');

        const signIn = asked[0];

        assert.ok(signIn.url.endsWith('/ext/login'),
                  `the password went to ${signIn.url}`);

        assert.equal(signIn.init.method, 'POST');
        assert.equal((signIn.init.headers as Record<string, string>)['Content-Type'],
                     'application/x-www-form-urlencoded');

        // "login", not "username": the field is named by the HTTPExt API.
        assert.equal(signIn.init.body, 'login=root&password=hunter2');

        // The password is never repeated to the second request.
        assert.ok(!asked.slice(1).some(call => String(call.init.body ?? '').includes('hunter2')),
                  'the password was sent more than once');

    });

    it('answers with what the account may do, not with what the sign-in said', async () => {

        let call = 0;

        fetchThat(() => {
            call++;
            return Promise.resolve(
                call === 1
                    // What the HTTPExt API answers: its own shape, no roles.
                    ? new Response(JSON.stringify({ '@context': '', description: 'signed in' }),
                                   { status: 201, headers: { 'Content-Type': 'application/json' } })
                    : new Response(JSON.stringify({ username: 'root', roles: ['cpo'], permissions: ['runDiagnostics'] }),
                                   { status: 200, headers: { 'Content-Type': 'application/json' } })
            );
        });

        const me = await api.auth.login('root', 'hunter2');

        assert.deepEqual(me.roles, ['cpo']);
        assert.ok(asked[1].url.endsWith('/auth/me'));

    });

    it('says what the HTTPExt API said when it refuses', async () => {

        fetchThat(() => Promise.resolve(
            new Response(JSON.stringify({ '@context': '', description: 'You do not have access to any organization!' }),
                         { status: 401, headers: { 'Content-Type': 'application/json' } })
        ));

        // Its refusals carry "description" where ours carry "error"; somebody
        // who just typed a password has to be told which it was.
        await assert.rejects(
            () => api.auth.login('root', 'hunter2'),
            (problem: unknown) => problem instanceof ApiError &&
                                  problem.message === 'You do not have access to any organization!'
        );

    });

});
