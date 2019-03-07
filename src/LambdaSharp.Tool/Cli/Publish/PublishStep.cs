/*
 * MindTouch λ#
 * Copyright (C) 2018-2019 MindTouch, Inc.
 * www.mindtouch.com  oss@mindtouch.com
 *
 * For community documentation and downloads visit mindtouch.com;
 * please review the licensing section.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Amazon.CloudFormation;
using Amazon.CloudFormation.Model;
using LambdaSharp.Tool.Model;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LambdaSharp.Tool.Cli.Publish {

    public class PublishStep : AModelProcessor {

        //--- Constructors ---
        public PublishStep(Settings settings, string sourceFilename) : base(settings, sourceFilename) { }

        //--- Methods---
        public async Task<string> DoAsync(string cloudformationFile, bool forcePublish, bool publishToServerlessApplicationRepository) {

            // make sure there is a deployment bucket
            if(Settings.DeploymentBucketName == null) {
                LogError("missing deployment bucket", new LambdaSharpToolConfigException(Settings.ToolProfile));
                return null;
            }

            // load cloudformation template
            if(!File.Exists(cloudformationFile)) {
                LogError("folder does not contain a CloudFormation file for publishing");
                return null;
            }

            // load cloudformation file
            var manifest = await new ModelManifestLoader(Settings, "cloudformation.json").LoadFromFileAsync(cloudformationFile);
            if(manifest == null) {
                return null;
            }

            // publish module
            return await new ModelPublisher(Settings, cloudformationFile).PublishAsync(manifest, forcePublish, publishToServerlessApplicationRepository );
        }

        public string ConvertFileToServerlessApplicationRepositoryFormat(string cloudformationFile) {
            var doc = JObject.Parse(File.ReadAllText(cloudformationFile));

            // add SAM transform
            doc.Property("AWSTemplateFormatVersion").AddAfterSelf(new JProperty("Transform", JToken.FromObject("AWS::Serverless-2016-10-31")));

            // remove 'DeploymentBucketName' parameter
            doc.SelectToken("$.Parameters.DeploymentBucketName").Parent.Remove();
            doc.SelectToken("$.Metadata.AWS::CloudFormation::Interface.ParameterLabels.DeploymentBucketName").Parent.Remove();
            doc.SelectToken("$.Metadata.AWS::CloudFormation::Interface.ParameterGroups[*].Parameters[?(@ == 'DeploymentBucketName')]").Remove();

            // replace all '!Ref DeploymentBucketName' occurrences with deployment bucket name
            doc.Descendants()
                .OfType<JObject>()
                .Where(obj =>
                    (obj.Property("Ref")?.Value is JValue value)
                    && (value.Value is string text)
                    && (text == "DeploymentBucketName")
                )
                .ToList()
                .ForEach(obj => obj.Replace(JToken.FromObject(Settings.DeploymentBucketName)));

            // return converted JSON document
            return doc.ToString(Formatting.Indented);
       }
    }
}