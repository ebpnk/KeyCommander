# KeyCommander
Key generation and activation plugin with command groups

# Configs 
```
{
  "CommandGroups": {
    "VIP": [
      "vip_remove {userid}",
      "vip_give {userid} {time} {item_group}"
    ],
    "KIT": [
      "css_kit_add {steamid} {item_group} {time}"
    ]
  },
  "DatabaseHost": "",
  "DatabasePort": 3306,
  "DatabaseUser": "",
  "DatabasePassword": "",
  "DatabaseName": "",
  "ConfigVersion": 1
}
```
# Commands

```
css_addkey "volume" "config group" "time" "itemGroup"
Example: css_addkey "10" "VIP" "0" "vipgold"

css_keydel <group_name>
Example: css_keydel KIT


Activation:
css_key "key"
!key code
```
