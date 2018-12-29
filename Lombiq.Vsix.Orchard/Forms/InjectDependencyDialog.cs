﻿using Lombiq.Vsix.Orchard.Models;
using Lombiq.Vsix.Orchard.Services.DependencyInjector;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace Lombiq.Vsix.Orchard.Forms
{
    public partial class InjectDependencyDialog : Form
    {
        private readonly IEnumerable<IFieldNameFromDependencyGenerator> _fieldNameGenerators;
        private readonly IEnumerable<IDependencyNameProvider> _dependencyNameProviders;
        private readonly string _className;


        public string DependencyName => dependencyNameTextBox.Text;


        public InjectDependencyDialog(
            IEnumerable<IFieldNameFromDependencyGenerator> fieldNameGenerators,
            IEnumerable<IDependencyNameProvider> dependencyNameProviders,
            string className = "")
        {
            _fieldNameGenerators = fieldNameGenerators;
            _dependencyNameProviders = dependencyNameProviders;
            _className = className;

            InitializeComponent();
        }


        public DependencyInjectionData GetDependencyInjectionData() =>
            new DependencyInjectionData
            {
                FieldName = fieldNameTextBox.Text,
                FieldType = fieldTypeTextBox.Text,
                ConstructorParameterName = parameterNameTextBox.Text,
                ConstructorParameterType = parameterTypeTextBox.Text
            };


        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            ActiveControl = dependencyNameTextBox;
            
            var suggestedDependencyNames = new AutoCompleteStringCollection();
            suggestedDependencyNames.AddRange(
                _dependencyNameProviders
                    .OrderBy(provider => provider.Priority)
                    .SelectMany(provider => provider.GetDependencyNames(_className))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray());
            dependencyNameTextBox.AutoCompleteCustomSource = suggestedDependencyNames;
            dependencyNameTextBox.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
            dependencyNameTextBox.AutoCompleteSource = AutoCompleteSource.CustomSource;
        }


        private DependencyInjectionData GenerateInjectedDependency(string dependency, bool useShortName = false)
        {
            var fieldNameGenerator = _fieldNameGenerators
                .OrderByDescending(service => service.Priority)
                .First(service => service.CanGenerate(dependency));

            return fieldNameGenerator.Generate(dependency, useShortName);
        }

        private void DependencyNameTextBoxTextChanged(object sender, EventArgs e)
        {
            var injectedDependency = DependencyName.Length == 0 ? 
                null : GenerateInjectedDependency(DependencyName, generateShortFieldNameCheckBox.Checked);

            fieldNameTextBox.Text = injectedDependency?.FieldName ?? "";
            fieldTypeTextBox.Text = injectedDependency?.FieldType ?? "";
            parameterNameTextBox.Text = injectedDependency?.ConstructorParameterName ?? "";
            parameterTypeTextBox.Text = injectedDependency?.ConstructorParameterType ?? "";
        }
    }
}
