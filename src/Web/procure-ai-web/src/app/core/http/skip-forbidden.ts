import { HttpContextToken } from '@angular/common/http';

/**
 * Set on a call whose 403 is part of the page's own story (a secondary section the user may not read, a refused write):
 * the caller handles the error and the page stays where it is, instead of the forbidden page taking over.
 */
export const SKIP_FORBIDDEN_REDIRECT = new HttpContextToken<boolean>(() => false);
