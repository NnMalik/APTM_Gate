Offline PostgreSQL 16 packages for the APTM Gate NUC
====================================================

setup.sh installs EVERY .deb in this folder together (dpkg -i *.deb) so PostgreSQL
works with no internet. You must drop the COMPLETE dependency set here -- not just the
"postgresql" package, but everything apt would normally pull in:

  postgresql-16, postgresql-client-16, postgresql-common, postgresql-client-common,
  libpq5, ssl-cert, and a few transitive libs (libllvm*, libxslt1.1, etc.)

The download machine MUST match the NUC's Ubuntu version and CPU arch (amd64).
Ubuntu 24.04 ships PostgreSQL 16 in its default repos. Ubuntu 22.04 does NOT --
you must add the PGDG repo first (see the bottom of this file).


Docker is NOT required. Pick whichever option fits:


OPTION A -- skip offline entirely (easiest)
-------------------------------------------
Give the NUC internet for ~2 minutes during setup.sh. The script installs
PostgreSQL online automatically. Nothing to place in this folder.


OPTION B -- harvest from ONE identical NUC (recommended when air-gapped)
-----------------------------------------------------------------------
Your gate NUCs share the same Ubuntu image, so the dependency set is identical.
On the first NUC (or any box on the same image) with internet:

  sudo apt-get clean
  sudo apt-get update
  sudo apt-get install --download-only postgresql postgresql-contrib
  sudo cp /var/cache/apt/archives/*.deb /media/<usb>/.../prerequisites/postgresql/

Those .debs are exactly what a stock NUC is missing -- copy them to the other
NUCs and install offline. (This works precisely because the machines match. On a
random dev laptop that already has half the deps, --download-only would skip them
and you'd get an incomplete set -- that's the only reason to prefer Docker.)


OPTION C -- Docker (only if building on a mismatched machine)
------------------------------------------------------------
A clean container has nothing pre-installed = guaranteed-complete closure.
Match the tag to your NUC, e.g. 24.04:

  mkdir -p pg-debs
  docker run --rm -v "$PWD/pg-debs:/debs" ubuntu:24.04 bash -c '
    apt-get update &&
    apt-get install -y --download-only postgresql postgresql-contrib &&
    cp /var/cache/apt/archives/*.deb /debs/'

Then copy everything from pg-debs/ into THIS folder.


VERIFY on the NUC after setup.sh runs
-------------------------------------
  psql --version            # -> psql (PostgreSQL) 16.x
  systemctl status postgresql


Ubuntu 22.04 ONLY -- pull PostgreSQL 16 from PGDG inside the container
----------------------------------------------------------------------
  mkdir -p pg-debs
  docker run --rm -v "$PWD/pg-debs:/debs" ubuntu:22.04 bash -c '
    apt-get update &&
    apt-get install -y curl ca-certificates gnupg lsb-release &&
    install -d /usr/share/postgresql-common/pgdg &&
    curl -fsSL https://www.postgresql.org/media/keys/ACCC4CF8.asc \
      -o /usr/share/postgresql-common/pgdg/apt.postgresql.org.asc &&
    echo "deb [signed-by=/usr/share/postgresql-common/pgdg/apt.postgresql.org.asc] \
http://apt.postgresql.org/pub/repos/apt jammy-pgdg main" \
      > /etc/apt/sources.list.d/pgdg.list &&
    apt-get update &&
    apt-get install -y --download-only postgresql-16 postgresql-contrib &&
    cp /var/cache/apt/archives/*.deb /debs/'
