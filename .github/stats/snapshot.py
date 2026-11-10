# Saves release download counts to the stats branch (checked out in the folder given as the only argument).
# releases.json: every release's asset counts, kept after a release is deleted. downloads.csv: one row per day, rewritten through the day.
# people = Setup/Portable downloads, less the one automatic download GitHub logs for every new file, less the developer's own (OWN_PORTABLE_PER_RELEASE).
import csv, datetime, json, os, sys, urllib.request
stats_dir, repo = sys.argv[1], os.environ['GITHUB_REPOSITORY']
own_portable_per_release = int(os.environ.get('OWN_PORTABLE_PER_RELEASE', '0'))
releases_path, csv_path = os.path.join(stats_dir, 'releases.json'), os.path.join(stats_dir, 'downloads.csv')
today = datetime.datetime.now(datetime.timezone.utc).date()

def fetch_releases():
    found, page = [], 1
    while True:
        request = urllib.request.Request(f'https://api.github.com/repos/{repo}/releases?per_page=100&page={page}', headers={'Accept': 'application/vnd.github+json', 'Authorization': 'Bearer ' + os.environ['GITHUB_TOKEN']})
        with urllib.request.urlopen(request, timeout=60) as response: batch = json.load(response)
        if not batch: return found
        found += batch; page += 1

saved = json.load(open(releases_path)) if os.path.exists(releases_path) else {}
live = fetch_releases()
if not live: sys.exit('GitHub returned no releases; nothing saved')
for release in live:
    entry = saved.setdefault(release['tag_name'], {'assets': {}})
    entry['published'], entry['prerelease'] = release['published_at'] or release['created_at'], release['prerelease']
    entry.pop('deleted', None)
    for asset in release['assets']: entry['assets'][asset['name']] = max(asset['download_count'], entry['assets'].get(asset['name'], 0))
live_tags = {release['tag_name'] for release in live}
for tag, entry in saved.items():
    if tag not in live_tags: entry.setdefault('deleted', today.isoformat())

def people(entry):
    total = 0
    for name, count in entry['assets'].items():
        if not name.endswith('.exe'): continue
        own = own_portable_per_release if name.endswith('-Portable.exe') else 0
        total += max(0, count - 1 - own)
    return total

installers = sum(count for entry in saved.values() for name, count in entry['assets'].items() if name.endswith('.exe'))
updates = sum(count for entry in saved.values() for name, count in entry['assets'].items() if name.endswith('-delta.nupkg'))
row = {'date': today.isoformat(), 'people': sum(people(entry) for entry in saved.values()), 'installers': installers, 'updates': updates, 'releases': len(saved), 'estimated': 0}
fields = list(row)
rows = list(csv.DictReader(open(csv_path))) if os.path.exists(csv_path) else []
if not rows:
    # First run: estimate earlier days by giving each release's downloads to the day it came out (most come while it's the latest)
    by_day = {}
    for entry in saved.values(): day = entry['published'][:10]; by_day[day] = by_day.get(day, 0) + people(entry)
    day, running = datetime.date.fromisoformat(min(by_day)), 0
    while day < today: running += by_day.get(day.isoformat(), 0); rows.append({'date': day.isoformat(), 'people': running, 'installers': '', 'updates': '', 'releases': '', 'estimated': 1}); day += datetime.timedelta(days=1)
rows = [existing for existing in rows if existing['date'] != row['date']] + [row]
json.dump(saved, open(releases_path, 'w'), indent=1, sort_keys=True)
with open(csv_path, 'w', newline='') as file: writer = csv.DictWriter(file, fields, lineterminator='\n'); writer.writeheader(); writer.writerows(rows)
print(row)
