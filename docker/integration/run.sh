#!/bin/sh
set -eu
# Synthetic credentials and short-lived trust exist only in this disposable test container.
openssl req -x509 -newkey rsa:2048 -nodes -days 1 -keyout /tmp/ca.key -out /usr/local/share/ca-certificates/obs-imap-fixture.crt -subj /CN=OBS-IMAP-Test-CA -addext 'crlDistributionPoints=URI:http://127.0.0.1:18080/ca.crl' >/dev/null 2>&1
openssl req -newkey rsa:2048 -nodes -keyout /tmp/imap.key -out /tmp/imap.csr -subj /CN=localhost >/dev/null 2>&1
printf 'subjectAltName=DNS:localhost\nextendedKeyUsage=serverAuth\ncrlDistributionPoints=URI:http://127.0.0.1:18080/ca.crl\n' > /tmp/extensions
openssl x509 -req -in /tmp/imap.csr -CA /usr/local/share/ca-certificates/obs-imap-fixture.crt -CAkey /tmp/ca.key -CAcreateserial -out /tmp/imap.crt -days 1 -extfile /tmp/extensions >/dev/null 2>&1
chmod 600 /tmp/imap.key /tmp/ca.key
update-ca-certificates >/dev/null 2>&1
mkdir -p /tmp/crl-public
touch /tmp/ca-index
printf '1000\n' > /tmp/crlnumber
cat > /tmp/ca.conf <<'EOF'
[ca]
default_ca = local
[local]
database = /tmp/ca-index
private_key = /tmp/ca.key
certificate = /usr/local/share/ca-certificates/obs-imap-fixture.crt
default_md = sha256
default_crl_days = 1
crlnumber = /tmp/crlnumber
EOF
openssl ca -gencrl -config /tmp/ca.conf -out /tmp/ca.crl.pem >/dev/null 2>&1
openssl crl -in /tmp/ca.crl.pem -outform DER -out /tmp/crl-public/ca.crl
python3 -m http.server 18080 --bind 127.0.0.1 --directory /tmp/crl-public >/dev/null 2>&1 &
crl_pid=$!
mkdir -p /srv/mail/fixture/Maildir/new /srv/mail/fixture/Maildir/cur /srv/mail/fixture/Maildir/tmp
cp docker/integration/messages/*.eml /srv/mail/fixture/Maildir/new/
python3 - <<'PY'
from pathlib import Path
folder = Path('/srv/mail/fixture/Maildir/.Large')
for part in ('new', 'cur', 'tmp'):
    (folder / part).mkdir(parents=True, exist_ok=True)
for number in range(1, 100001):
    (folder / 'new' / str(number)).write_text(
        f'From: large@example.test\r\nTo: fixture@example.test\r\nSubject: large fixture {number}\r\n'
        f'Message-ID: <large-{number}@example.test>\r\nContent-Type: text/plain\r\n\r\nSynthetic large-folder fixture.\r\n',
        encoding='ascii')
PY
chown -R 2000:2000 /srv/mail
dovecot -F &
server_pid=$!
trap 'kill "$server_pid" "$crl_pid" 2>/dev/null || true' EXIT INT TERM
i=0
until openssl s_client -connect 127.0.0.1:1993 -servername localhost </dev/null >/dev/null 2>&1; do
  i=$((i + 1))
  if [ "$i" -ge 30 ]; then cat /tmp/dovecot.log; exit 1; fi
  sleep 1
done
dotnet test OBS-IT.IMAP.slnx --no-restore --configuration Release --logger 'console;verbosity=minimal'
