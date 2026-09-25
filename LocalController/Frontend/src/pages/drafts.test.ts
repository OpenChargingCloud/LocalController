/**
 * Every page with a form asks before what is typed into it is thrown away.
 *
 * Run with `npm test`. Node's runner has no browser to open a page in, so what
 * is asked here is the pages' source: that each page with a form says, for as
 * long as it is open, whether it is holding anything, and that its Reload asks
 * first. What that does in a browser was measured when it was put in: five
 * pages threw a typed address, port, station, subject or authority away on one
 * click of Reload or of the menu, without a word, and ask now.
 *
 * It is here because of how the five came about. The pages taken over from the
 * vehicle had it, and the ones written for the local controller alone did not.
 */

import { strict as assert }           from 'node:assert';
import { readdirSync, readFileSync }  from 'node:fs';
import { describe, it }               from 'node:test';


/** Pages whose form is not a draft: signing in is the way in, and nothing typed there is the local controller's to lose. */
const notDrafts = new Set([ 'login.ts' ]);

const here      = new URL('./', import.meta.url);

const pages     = readdirSync(here).
                      filter(name => name.endsWith('.ts') && !name.endsWith('.test.ts')).
                      map(name => ({ name, source: readFileSync(new URL(name, here), 'utf-8') }));

const withForms = pages.filter(page => page.source.includes('<form') && !notDrafts.has(page.name));


describe('every page with a form', () => {

    it('is found at all, so that what follows is not said of nothing', () => {
        assert.ok(withForms.length >= 7, `only ${withForms.length} page(s) with a form were found`);
    });

    for (const page of withForms) {

        it(`${page.name} says whether it is holding anything`, () => {
            assert.match(page.source, /unsaved\.heldBy\(/,
                         `${page.name} has a form and never says when it is holding a draft, so leaving it asks nothing`);
        });

        if (page.source.includes('id="reload"'))
            it(`${page.name} asks before its Reload throws a draft away`, () => {
                assert.match(page.source, /unsaved\.mayBeLost\(\)/,
                             `${page.name} has a form and a Reload that redraws it without asking`);
            });

    }

});
