def sort_key(name):
    # C# logic: aItems && !bItems returns -1, which means items.json comes first.
    # Then tracker.json.
    # Then alphabetical.
    if name.lower().endswith("items.json"):
        return (0, name)
    elif name.lower().endswith("tracker.json"):
        return (1, name)
    else:
        return (2, name)

names = ["layouts/broadcast.json", "layouts/items.json", "layouts/settings.json", "layouts/tracker.json"]
names.sort(key=sort_key)
for n in names: print(n)
