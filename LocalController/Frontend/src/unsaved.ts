/**
 * What is on a page that the local controller has not been told about.
 *
 * Measured: an EVSE added to the list, then one click on Reload - gone, with
 * no question and no message, and "Discard changes" greyed out again as if
 * there had never been anything to discard. The same work, and one click on
 * "RFID" in the menu on the left - gone, and nothing on the way out said so.
 *
 * The pages already knew: three of them keep a "dirty" flag and offer a
 * "Discard changes" button, which is the deliberate way to throw work away.
 * What was missing is that two ordinary actions did the same thing by
 * accident, from the other side of the screen, without saying anything.
 *
 * So one page at a time says here whether it is holding something, and the
 * three ways out of a page - its own Reload, a link inside the interface, and
 * leaving the browser page altogether - ask first.
 */

/** How a page is asked whether it is holding anything. */
type Asking = () => boolean;

/**
 * The page that is open. One at a time, because one is open at a time: the
 * router renders the next page only after the previous one has been cleaned
 * up, and a page that is gone is holding nothing anybody can still save.
 */
let asking: Asking | null = null;


/** What somebody is asked before their work is thrown away. */
export const theQuestion = 'There are changes on this page that the local controller has not been told about.\n\n' +
                           'Leave them behind?';


export const unsaved = {

    /**
     * A page says, for as long as it is open, how to ask whether it is
     * holding anything. What comes back releases it again, and belongs in the
     * page's cleanup.
     */
    heldBy(Ask: Asking): () => void {

        asking = Ask;

        return () => {
            if (asking === Ask)
                asking = null;
        };

    },

    /** Whether the page that is open is holding anything. */
    any(): boolean {

        try
        {
            return asking?.() ?? false;
        }
        catch
        {
            // A page that cannot answer is a page halfway through drawing
            // itself, which is not a page anybody is typing into.
            return false;
        }

    },

    /**
     * Ask before it is thrown away; true to go ahead.
     *
     * Nothing to lose means nothing to ask about, so the ordinary case costs
     * a click and no dialog.
     */
    mayBeLost(): boolean {

        if (!unsaved.any())
            return true;

        try
        {
            return confirm(theQuestion);
        }
        catch
        {
            // Somewhere the question cannot be put. Of the two ways to be
            // wrong, trapping somebody on a page they want to leave is the
            // worse one, so it gives way.
            return true;
        }

    }

};


/** Anything on a page that holds a value somebody may have changed. */
interface Changeable {
    tagName:         string;
    type?:           string;
    value?:          string;
    defaultValue?:   string;
    checked?:        boolean;
    defaultChecked?: boolean;
    options?:        readonly { selected: boolean; defaultSelected: boolean }[];
}

/**
 * Whether anything under here has been typed into since the page drew it.
 *
 * The browser keeps this for nothing: what the page wrote into the HTML is the
 * field's default, and what is in it now is its value. That is exactly the
 * question for a page whose form is its draft.
 *
 * It is not the question for a page that edits a list, because such a page
 * redraws itself as the list is edited, and every field in it then matches
 * what it was drawn with again. Those pages keep a flag of their own and
 * answer with that instead.
 */
export function typedSinceDrawn(Root: ParentNode | null): boolean {

    if (Root === null)
        return false;

    for (const control of Root.querySelectorAll('input, textarea, select') as unknown as Iterable<Changeable>)
    {

        if (control.tagName === 'SELECT') {
            if ([...(control.options ?? [])].some(option => option.selected !== option.defaultSelected))
                return true;
        }

        else if (control.type === 'checkbox' || control.type === 'radio') {
            if (control.checked !== control.defaultChecked)
                return true;
        }

        else if (control.value !== control.defaultValue)
            return true;

    }

    return false;

}
