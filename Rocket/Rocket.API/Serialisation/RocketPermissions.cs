using System;
using System.Collections.Generic;
using System.Xml.Serialization;

namespace Rocket.API.Serialisation
{
    [Serializable]
    public class RocketPermissions : IDefaultable
    {
        public RocketPermissions()
        {
        }

        [XmlElement("DefaultGroup")]
        public string DefaultGroup = "default";

        [XmlArray("Groups")]
        [XmlArrayItem(ElementName = "Group")]
        public List<RocketPermissionsGroup> Groups = [];

        public void LoadDefaults()
        {
            DefaultGroup = "default";
            Groups =
            [
                new RocketPermissionsGroup("default", "Guest", null, [],
                    [new Permission("p"), new Permission("compass"), new Permission("rocket")], "white"),
                new RocketPermissionsGroup("vip", "VIP", "default", ["76561198016438091"],
                    [new Permission("effect"), new Permission("heal", 120), new Permission("v", 30)], "FF9900")
            ];
        }
    }
}
