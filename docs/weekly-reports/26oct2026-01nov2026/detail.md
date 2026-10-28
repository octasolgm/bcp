# Product update (26 Oct 2026)

## Dev environment

We published a fresh API and web build to the dev Azure environment from release branch release/2026.10.26.01 (build label 2026.10.26.01 for both apps).

Use https://bcp-web-dev.azurewebsites.net as usual. After login, hard refresh your browser (Ctrl+Shift+R) and open Settings to confirm the build version matches on web and API.

# Product update (28 Oct 2026)

## Source code security clean-up

Our code hosting account had been accessed by an outside party, who pushed altered copies of recent work with harmful scripts hidden inside. We checked every copy of the code, on our own machines and in the hosted repository. The working copies used to build and deploy the product were clean, so no deployed build was affected.

All affected branches in the hosted repository have now been restored to the genuine code, and a full re-check found no remaining harmful files. Access credentials have been reset and two-factor sign-in is now in place.

## Being worked on

We are adding a routine check that runs before any code is pulled from the hosted repository, so that any future tampering is caught before it reaches a developer machine.
