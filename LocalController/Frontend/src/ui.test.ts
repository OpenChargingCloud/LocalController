/**
 * The rules the web interface's pages share, asked directly.
 *
 * Run with `npm test`, which is Node's own runner reading the TypeScript as it
 * stands - no bundler, no browser, no dependency that is not already here.
 *
 * What is pinned is what was measured to be wrong: a page that threw away
 * everything typed while a save was on its way, and said "Saved, and in
 * effect." over the hole it had just made.
 */

import { strict as assert }  from 'node:assert';
import { describe, it }      from 'node:test';

import { beingSaved, whileSaving } from './ui.ts';


/** Something on a page that can be switched off; all whileSaving cares about. */
const control = (disabled = false) => ({ disabled });

/** A page holding exactly those controls. */
const page = (controls: { disabled: boolean }[]) =>
    ({ querySelectorAll: () => controls }) as unknown as HTMLElement;

/** Somewhere for it to say what is happening. */
const saying = () => ({ textContent: 'whatever was there before' }) as HTMLElement;


describe('a page while the local controller is being told', () => {

    it('cannot be typed into, which is what makes losing the typing impossible', async () => {

        const live = [control(), control(), control()];
        let whileItRan: boolean[] = [];

        await whileSaving(page(live), null, async () => {
            whileItRan = live.map(one => one.disabled);
        });

        assert.deepEqual(whileItRan, [true, true, true]);

    });

    it('says so, because a page that does not react is one people press again', async () => {

        const note = saying();
        let said   = '';

        await whileSaving(page([control()]), note, async () => { said = note.textContent ?? ''; });

        assert.equal(said, beingSaved);

    });

    it('comes back afterwards', async () => {

        const live = [control(), control()];
        const note = saying();

        await whileSaving(page(live), note, async () => undefined);

        assert.deepEqual(live.map(one => one.disabled), [false, false]);

        // And what is happening is no longer happening; what it turned into is
        // the page's to say.
        assert.equal(note.textContent, '');

    });

    it('comes back when it failed as well, or the page would be dead', async () => {

        const live = [control(), control()];
        const note = saying();

        await assert.rejects(() =>
            whileSaving(page(live), note, () => Promise.reject(new Error('the local controller said no'))));

        assert.deepEqual(live.map(one => one.disabled), [false, false]);
        assert.equal(note.textContent, '');

    });

    it('leaves what was already switched off switched off', async () => {

        // A role that may look but not change must not be handed a live form
        // by a save that failed.
        const notAllowed = control(true);
        const nothingToDiscard = control(true);
        const ordinary   = control();

        await assert.rejects(() =>
            whileSaving(page([notAllowed, nothingToDiscard, ordinary]), null,
                        () => Promise.reject(new Error('no'))));

        assert.equal(notAllowed.disabled,       true);
        assert.equal(nothingToDiscard.disabled, true);
        assert.equal(ordinary.disabled,         false);

    });

    it('hands back what the local controller answered, untouched', async () => {

        const answer = await whileSaving(page([control()]), null,
                                         () => Promise.resolve({ uplinkPowerLimit_kW: 150 }));

        assert.deepEqual(answer, { uplinkPowerLimit_kW: 150 });

    });

    it('and hands on what it refused, so the page can say why', async () => {

        await assert.rejects(
            () => whileSaving(page([control()]), null,
                              () => Promise.reject(new Error("'display' needs both 'dimFrom' and 'dimUntil'."))),
            /dimFrom/
        );

    });

});
